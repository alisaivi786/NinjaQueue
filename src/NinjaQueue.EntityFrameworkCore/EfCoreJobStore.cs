using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NinjaQueue.EntityFrameworkCore.Data;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>
/// <see cref="IJobStore"/> over the host's <typeparamref name="TContext"/> (SQL Server and PostgreSQL). All job SQL is raw
/// ADO.NET on the unit of work's connection and transaction, so it joins the host's transaction (Stage, fenced steps) and
/// can use ON CONFLICT / HOLDLOCK / SKIP LOCKED / READPAST, which tracked EF inserts cannot.
/// </summary>
internal sealed partial class EfCoreJobStore<TContext>(
    IJobTransactionLocator<TContext> transactions,
    JobUnitOfWork<TContext> unitOfWork,
    IOptions<NinjaQueueOptions> options) : IJobStore
    where TContext : DbContext
{
    private const int EnqueueChunkSize = 500;
    private readonly NinjaQueueOptions _options = options.Value;
    private JobSql? _sql;

    // ---- enqueue ---------------------------------------------------------------------------------------------------

    public async Task<JobId> StageAsync(NewJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (transactions.FindTransactionalContext() is not { } context || context.Database.CurrentTransaction is not { } current)
        {
            throw new NoAmbientTransactionException();
        }

        JobSession<TContext> session = new(context);
        JobSql sql = Sql(session.DbContext);
        DbTransaction transaction = current.GetDbTransaction();
        if (IsPlain(job))
        {
            await InsertPlainJobsAsync(sql, session.Connection, transaction, [job], cancellationToken);
            return new JobId(job.GuidId);
        }

        return await InsertAsync(sql, session.Connection, transaction, job, cancellationToken);
    }

    public async Task<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        List<JobId> ids = [with(jobs.Count)];
        foreach (NewJob[] chunk in jobs.Chunk(EnqueueChunkSize))
        {
            List<JobId> chunkIds = [with(chunk.Length)];
            await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                chunkIds.Clear(); // an execution strategy may run the transaction again
                JobSql sql = Sql(session.DbContext);
                DbTransaction transaction = session.Transaction!.GetDbTransaction();

                // Runs of plain jobs (nothing to deduplicate, reserve or link) go set-based; a keyed job or a continuation
                // takes the per-job path with its key locks. Runs keep the caller's order, so ids and FIFO keep it too.
                int start = 0;
                while (start < chunk.Length)
                {
                    int end = start;
                    while (end < chunk.Length && IsPlain(chunk[end]))
                    {
                        end++;
                    }

                    if (end > start)
                    {
                        await InsertPlainJobsAsync(sql, session.Connection, transaction, chunk[start..end], ct);
                        chunkIds.AddRange(chunk[start..end].Select(j => new JobId(j.GuidId)));
                        start = end;
                        continue;
                    }

                    chunkIds.Add(await InsertAsync(sql, session.Connection, transaction, chunk[start], ct));
                    start++;
                }
            }, cancellationToken);
            ids.AddRange(chunkIds);
        }

        return ids;
    }

    /// <summary>A job with no idempotency key, unique key or parent: its insert can never conflict, so it can go set-based.</summary>
    private static bool IsPlain(NewJob job) => job.IdempotencyKey is null && job.UniqueKey is null && job.ContinueAfter is null;

    private static async Task InsertPlainJobsAsync(JobSql sql, DbConnection connection, DbTransaction transaction, IReadOnlyList<NewJob> jobs, CancellationToken ct)
    {
        foreach (NewJob[] rows in jobs.Chunk(sql.MaxRowsPerInsert))
        {
            string json = JobRowsJson(rows);
            await using DbCommand insert = JobCommand.Create(connection, transaction, sql.InsertJobs);
            insert.With("rows", json, DbType.String).With("event_code", JobEventCodes.JobEnqueued, DbType.String);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The <see cref="JobSql.InsertJobs"/> rows as one JSON array (one parameter, whatever the batch size).</summary>
    private static string JobRowsJson(IReadOnlyList<NewJob> jobs)
    {
        System.Buffers.ArrayBufferWriter<byte> buffer = new(jobs.Count * 256);
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartArray();
            for (int i = 0; i < jobs.Count; i++)
            {
                NewJob job = jobs[i];
                json.WriteStartObject();
                json.WriteNumber("ord", i);
                json.WriteString("guid_id", job.GuidId);
                json.WriteString("job_type", job.JobType);
                json.WriteString("queue", job.Queue);
                WriteGuid(json, "tenant_id", job.TenantId);
                json.WriteString("tenant_scope", job.TenantScope);
                json.WriteString("payload", job.Payload);
                json.WriteString("concurrency_key", job.ConcurrencyKey);
                json.WriteNumber("max_attempts", job.MaxAttempts);
                if (job.RunAtUtc is { } runAt)
                {
                    // ISO 8601 with an explicit +00:00 offset: both OPENJSON and PostgreSQL's timestamptz parse it.
                    json.WriteString("run_at", runAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    json.WriteNull("run_at");
                }

                json.WriteString("correlation_id", job.CorrelationId);
                json.WriteString("causation_id", job.CausationId);
                json.WriteString("trace_parent", job.TraceParent);
                WriteGuid(json, "actor_id", job.ActorId);
                json.WriteString("source", job.Source);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void WriteGuid(Utf8JsonWriter json, string name, Guid? value)
        {
            if (value is { } v)
            {
                json.WriteString(name, v);
            }
            else
            {
                json.WriteNull(name);
            }
        }
    }

    private static async Task<JobId> InsertAsync(JobSql sql, DbConnection connection, DbTransaction transaction, NewJob job, CancellationToken ct)
    {
        if (job.ContinueAfter is not null)
        {
            // Continuations (§15.6, Release 2): EfCoreJobStore.Continuations.cs locks the parent, then decides the child.
            return await InsertContinuationAsync(sql, connection, transaction, job, ct);
        }

        return await InsertJobAsync(sql, connection, transaction, job, ct);
    }

    private static async Task<JobId> InsertJobAsync(JobSql sql, DbConnection connection, DbTransaction transaction, NewJob job, CancellationToken ct)
    {
        // SQL Server: per-key application locks (JobSql) serialize concurrent enqueues of the same key, never different keys,
        // and never raise a violation inside the caller's transaction. PostgreSQL: ON CONFLICT DO NOTHING, which never aborts the caller's transaction (§15.3).
        if (job.IdempotencyKey is not null && sql.IdempotencyProbe is { } idempotencyProbe)
        {
            if (await FindJobAsync(connection, transaction, idempotencyProbe, job, ct) is { } existing)
            {
                return await OnIdempotencyConflictAsync(sql, connection, transaction, job, existing, ct);
            }
        }

        if (job.UniqueKey is not null && sql.UniqueProbe is { } uniqueProbe)
        {
            if (await ScalarGuidAsync(KeyCommand(connection, transaction, uniqueProbe, job), ct) is { } holder)
            {
                return OnUniqueConflict(job, holder);
            }
        }

        long id;
        DateTimeOffset runAt;
        await using (DbCommand insert = JobCommand.Create(connection, transaction, sql.InsertJob)
            .With("guid_id", job.GuidId, DbType.Guid)
            .With("job_type", job.JobType, DbType.String)
            .With("queue", job.Queue, DbType.String)
            .With("tenant_id", job.TenantId, DbType.Guid)
            .With("tenant_scope", job.TenantScope, DbType.Guid)
            .With("payload", job.Payload, DbType.String)
            .With("idempotency_key", job.IdempotencyKey, DbType.String)
            .With("unique_key", job.UniqueKey, DbType.String)
            .With("concurrency_key", job.ConcurrencyKey, DbType.String)
            .With("max_attempts", job.MaxAttempts, DbType.Int32)
            .With("run_at", job.RunAtUtc, DbType.DateTimeOffset)
            .With("correlation_id", job.CorrelationId, DbType.String)
            .With("causation_id", job.CausationId, DbType.String)
            .With("trace_parent", job.TraceParent, DbType.String)
            .With("actor_id", job.ActorId, DbType.Guid)
            .With("source", job.Source, DbType.String))
        await using (DbDataReader reader = await insert.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                await reader.DisposeAsync();
                (long, Guid) existing = await FindJobAsync(connection, transaction, sql.FindIdempotent, job, ct)
                    ?? throw new InvalidOperationException("Idempotency conflict reported, but the existing job was not found.");
                return await OnIdempotencyConflictAsync(sql, connection, transaction, job, existing, ct);
            }

            id = reader.ReadLong(0);
            runAt = reader.ReadUtc(1);
        }

        if (job.UniqueKey is not null)
        {
            object? reserved;
            await using (DbCommand reserve = KeyCommand(connection, transaction, sql.ReserveUnique, job).Id(id))
            {
                reserved = await reserve.ExecuteScalarAsync(ct);
            }

            if (reserved is null or DBNull)
            {
                await JobCommand.Create(connection, transaction, sql.DeleteJob).Id(id).RunAsync(ct);
                Guid holder = await ScalarGuidAsync(KeyCommand(connection, transaction, sql.UniqueHolder, job), ct)
                    ?? throw new InvalidOperationException("Unique-key conflict reported, but the holding job was not found.");
                return OnUniqueConflict(job, holder);
            }
        }

        await JobCommand.Create(connection, transaction, sql.InsertQueueAndEvent)
            .Id(id)
            .With("queue", job.Queue, DbType.String)
            .With("job_type", job.JobType, DbType.String)
            .With("available_at", runAt, DbType.DateTimeOffset)
            .With("tenant_scope", job.TenantScope, DbType.Guid)
            .With("concurrency_key", job.ConcurrencyKey, DbType.String)
            .With("attempts_left", (short)job.MaxAttempts, DbType.Int16)
            .With("event_code", JobEventCodes.JobEnqueued, DbType.String)
            .With("actor_id", job.ActorId, DbType.Guid)
            .RunAsync(ct);
        return new JobId(job.GuidId);
    }

    private static async Task<JobId> OnIdempotencyConflictAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, NewJob job, (long Id, Guid GuidId) existing, CancellationToken ct)
    {
        if (job.OnIdempotencyConflict == IdempotencyConflict.Throw)
        {
            throw new JobConflictException($"A '{job.JobType}' job with idempotency key '{job.IdempotencyKey}' already exists.", new JobId(existing.GuidId));
        }

        await InsertEventAsync(sql, connection, transaction, existing.Id, JobEventCodes.JobDeduplicated, null, job.ActorId, null, ct);
        return new JobId(existing.GuidId);
    }

    private static JobId OnUniqueConflict(NewJob job, Guid holder)
        => job.OnUniqueKeyConflict == UniqueKeyConflict.Reject
            ? throw new JobConflictException($"Another active job holds unique key '{job.UniqueKey}'.", new JobId(holder))
            : new JobId(holder);

    private static async Task<(long Id, Guid GuidId)?> FindJobAsync(DbConnection connection, DbTransaction transaction, string sqlText, NewJob job, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(connection, transaction, sqlText)
            .With("tenant_scope", job.TenantScope, DbType.Guid)
            .With("job_type", job.JobType, DbType.String)
            .With("idempotency_key", job.IdempotencyKey, DbType.String);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.ReadLong(0), reader.GetGuid(1)) : null;
    }

    private static DbCommand KeyCommand(DbConnection connection, DbTransaction transaction, string sqlText, NewJob job)
        => JobCommand.Create(connection, transaction, sqlText)
            .With("tenant_scope", job.TenantScope, DbType.Guid)
            .With("unique_key", job.UniqueKey, DbType.String);

    private static async Task<Guid?> ScalarGuidAsync(DbCommand command, CancellationToken ct)
    {
        await using (command)
        {
            return await command.ExecuteScalarAsync(ct) is Guid value ? value : null;
        }
    }

    private static Task<int> InsertEventAsync(
        JobSql sql, DbConnection connection, DbTransaction? transaction, long jobId, string code, string? workerId, Guid? actorId, string? reason,
        CancellationToken ct, string? data = null)
        => JobCommand.Create(connection, transaction, sql.InsertEvent)
            .Id(jobId)
            .With("event_code", code, DbType.String)
            .With("worker_id", workerId, DbType.String)
            .With("actor_id", actorId, DbType.Guid)
            .With("reason", Truncate(reason, 1000), DbType.String)
            .With("data", data, DbType.String)
            .RunAsync(ct);

    // ---- claim (§15.2) ---------------------------------------------------------------------------------------------

    // The unkeyed batch claim; ClaimAsync (EfCoreJobStore.Claim.Concurrency.cs) adds the keyed claim (§14.5, Release 2).
    private Task<IReadOnlyList<ClaimedJob>> ClaimUnkeyedAsync(
        string queue, IReadOnlyList<string> jobTypes, string workerId, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobTypes);
        if (maxJobs <= 0 || jobTypes.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ClaimedJob>>([]);
        }

        return unitOfWork.UseTransactionAsync<IReadOnlyList<ClaimedJob>>(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection connection = session.Connection;
            DbTransaction transaction = session.Transaction!.GetDbTransaction();

            await using DbCommand claim = JobCommand.Create(connection, transaction, sql.Claim)
                .With("queue", queue, DbType.String)
                .With("n", maxJobs, DbType.Int32)
                .With("worker_id", workerId, DbType.String)
                .With("lease_ms", (int)leaseDuration.TotalMilliseconds, DbType.Int32)
                .With("event_code", JobEventCodes.JobClaimed, DbType.String);
            if (sql.IsPostgres)
            {
                claim.WithInferred("types", jobTypes.ToArray());
            }
            else
            {
                claim.With("types", JsonSerializer.Serialize(jobTypes), DbType.String);
            }

            return await ReadClaimedAsync(claim, workerId, ct);
        }, cancellationToken);
    }

    /// <summary>Reads the rows of <see cref="JobSql.Claim"/> / <see cref="JobSql.MarkLeased"/>, ordered by job id.</summary>
    private static async Task<List<ClaimedJob>> ReadClaimedAsync(DbCommand command, string workerId, CancellationToken ct)
    {
        List<ClaimedJob> claimed = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        do
        {
            // SQL Server: the batch's only result set is the final SELECT; skip anything before it defensively.
            if (reader.FieldCount < 16)
            {
                continue;
            }

            while (await reader.ReadAsync(ct))
            {
                claimed.Add(new ClaimedJob(
                    reader.ReadLong(0), new JobId(reader.GetGuid(3)), reader.GetString(4), reader.ReadGuidOrNull(5), reader.GetGuid(1), workerId,
                    reader.ReadUtc(2), reader.ReadInt(6), reader.ReadInt(15), reader.ReadInt(7), reader.ReadStringOrNull(8), reader.ReadStringOrNull(9),
                    reader.ReadStringOrNull(10), reader.ReadStringOrNull(11), reader.ReadInt(12), reader.ReadInt(13), reader.ReadUtc(14)));
            }
        }
        while (await reader.NextResultAsync(ct));

        return claimed;
    }

    // ---- fenced step (§15.1, §16.4, §17.1) -------------------------------------------------------------------------

    public async Task<FencedStepOutcome> RunFencedStepAsync(
        ClaimedJob job, FencedStepSettings settings, Func<string?, CancellationToken, Task<FencedStepResult>> step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(step);

        FencedStepOutcome? committed;
        bool registered = false;
        try
        {
            committed = await unitOfWork.UseSharedTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                EnsureGuardInstalled(session.DbContext);
                DbConnection connection = session.Connection;
                DbTransaction transaction = session.Transaction!.GetDbTransaction();

                // 1. The fence: the first statement. 0 rows = lease lost, nothing runs. 1 row = this transaction now holds the
                //    queue row lock until it ends, so no claim or recovery can take the job meanwhile.
                FenceResult fence = await FenceAsync(sql, connection, transaction, job, settings.LeaseDuration, ct);
                if (!fence.Matched)
                {
                    throw new FencedStepAbortException(FencedStepStatus.LeaseLost);
                }

                if (fence.CancelRequestedAtUtc is not null && settings.Interruptible)
                {
                    throw new FencedStepAbortException(FencedStepStatus.Cancelled);
                }

                // System.Transactions would enlist work outside the engine's transaction: detected, the step fails (§17.7).
                if (System.Transactions.Transaction.Current is not null)
                {
                    throw new FencedStepFailedException(new FencedContractViolationException(
                        "A System.Transactions TransactionScope is active around a fenced step. Fenced handlers use only the engine's transaction."));
                }

                await RegisterFencedStepAsync(sql, connection, transaction, job, settings.StepTimeout, ct);
                registered = true;

                int boundMs = (int)Math.Min(int.MaxValue, settings.StepTimeout.TotalMilliseconds);
                if (sql.StepBound(boundMs) is { } bound)
                {
                    await JobCommand.Create(connection, transaction, bound).RunAsync(ct);
                }

                // 2. The handler's step, with nested units of work joining this transaction and the guard watching it.
                FencedStepResult result;
                using (unitOfWork.EnterHandlerScope(session))
                using (FencedTransactionGuard.Enter(connection, transaction))
                {
                    try
                    {
                        result = await step(job.Checkpoint, ct);
                        await session.DbContext.SaveChangesAsync(ct);
                    }
                    catch (Exception ex) when (ex is not FencedStepAbortException)
                    {
                        throw new FencedStepFailedException(ex);
                    }
                }

                // 3. Finalization: the cancel check is the WHERE clause, atomic with the commit (§17.1).
                int matched = await JobCommand.Create(connection, transaction, result.IsComplete ? sql.FinalizeComplete : sql.FinalizeContinue)
                    .Id(job.Id)
                    .With("checkpoint", result.IsComplete ? job.Checkpoint : result.Checkpoint, DbType.String)
                    .With("progress_processed", result.ProgressProcessed, DbType.Int32)
                    .With("progress_total", result.ProgressTotal, DbType.Int32)
                    .With("interruptible", settings.Interruptible, DbType.Boolean)
                    .RunAsync(ct);
                if (matched != 1)
                {
                    throw new FencedStepAbortException(null);
                }

                if (!result.IsComplete)
                {
                    await FencedStepTestHooks.BeforeCommitAsync();
                    return new FencedStepOutcome(FencedStepStatus.Continued, result.Checkpoint);
                }

                // 4. Completion writes in the same transaction (Release 1: no continuation logic, §15.1).
                if (await JobCommand.Create(connection, transaction, sql.DeleteQueueRow).Id(job.Id).Token(job.LeaseToken).RunAsync(ct) != 1)
                {
                    throw new FencedStepAbortException(FencedStepStatus.LeaseLost);
                }

                await CloseAttemptAsync(sql, connection, transaction, job, JobAttemptOutcome.Succeeded, refund: false, null, null, null, ct);
                await InsertEventAsync(sql, connection, transaction, job.Id, JobEventCodes.JobSucceeded, job.WorkerId, null, null, ct);
                if (!settings.Interruptible)
                {
                    // The finalization ignored a pending cancel (§17.1 last row): the attempt succeeded, so the cancel was too late.
                    await JobCommand.Create(connection, transaction, sql.CancelTooLateIfRequested)
                        .Id(job.Id)
                        .With("event_code", JobEventCodes.JobCancelTooLate, DbType.String)
                        .With("worker_id", job.WorkerId, DbType.String)
                        .RunAsync(ct);
                }

                await JobCommand.Create(connection, transaction, sql.DeleteActiveKey).Id(job.Id).RunAsync(ct);
                await ReleaseContinuationsAsync(sql, connection, transaction, job.Id, JobStatus.Succeeded, ct);
                await FencedStepTestHooks.BeforeCommitAsync();
                return new FencedStepOutcome(FencedStepStatus.Completed);
            }, cancellationToken);
        }
        catch (FencedStepAbortException abort) when (abort.Status == FencedStepStatus.LeaseLost)
        {
            return new FencedStepOutcome(FencedStepStatus.LeaseLost);
        }
        catch (FencedStepAbortException)
        {
            // Rolled back. Zero rows (or a cancel seen at the fence) is never read as "cancel won" on its own: classify it.
            return await ClassifyAsync(job, cancellationToken);
        }
        catch (FencedStepFailedException failed)
        {
            return new FencedStepOutcome(FencedStepStatus.Failed, Error: failed.InnerException);
        }
#pragma warning disable CA1031 // The step transaction boundary: whatever ended it, it rolled back and the attempt must be recorded.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Thrown by the engine's own statements or the commit itself (a database failure, a fault-injection hook),
            // not by the handler. The transaction rolled back (or its commit is unknown, which the lease-checked outcome
            // write resolves: a committed completion deleted the queue row, so it reads as LeaseLost). Never let it escape
            // and strand the job leased until expiry: report the step as failed so the attempt is recorded.
            // An ambient TransactionScope makes the provider refuse the engine's own transaction before the fence: that is
            // the §17.7 contract violation, reported as such (permanent), not as a generic failure.
            Exception error = System.Transactions.Transaction.Current is not null
                ? new FencedContractViolationException(
                    "A System.Transactions TransactionScope is active around a fenced step. Fenced handlers use only the engine's transaction.", ex)
                : ex;
            return new FencedStepOutcome(FencedStepStatus.Failed, Error: error);
        }
#pragma warning restore CA1031
        finally
        {
            if (registered)
            {
                await UnregisterFencedStepSafeAsync(job);
            }
        }

        return committed;
    }

    /// <summary>The §17.1 classification of a step that could not commit, in its own short transaction.</summary>
    private async Task<FencedStepOutcome> ClassifyAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                DbConnection connection = session.Connection;
                DbTransaction transaction = session.Transaction!.GetDbTransaction();

                if (await LockLeasedJobAsync(sql, connection, transaction, job, ct) is not { } state)
                {
                    return new FencedStepOutcome(FencedStepStatus.LeaseLost);
                }

                if (state.Status == JobStatus.Processing && state.CancelRequestedAtUtc is not null)
                {
                    await CancelLeasedAsync(sql, connection, transaction, job, state, JobTerminalReason.AdminCancelled, ct);
                    return new FencedStepOutcome(FencedStepStatus.Cancelled);
                }

                // A status changed underneath the step, or an unexplained miss: never guess; record and leave it to recovery.
                await CloseAttemptAsync(sql, connection, transaction, job, JobAttemptOutcome.LeaseLost, refund: false, "StateConflict", $"Status {state.Status} at finalization.", null, ct);
                await InsertEventAsync(sql, connection, transaction, job.Id, JobEventCodes.JobStateConflict, job.WorkerId, null, $"Finalization matched 0 rows; status {state.Status}.", ct);
                return new FencedStepOutcome(FencedStepStatus.StateConflict);
            }, cancellationToken);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            return new FencedStepOutcome(FencedStepStatus.OwnershipUnconfirmed);
        }
    }

    private static bool IsDatabaseFailure(Exception ex) => DatabaseFailures.IsDatabaseFailure(ex);

    // ---- renew, progress, outcome ----------------------------------------------------------------------------------

    public async Task<FenceResult> RenewAsync(ClaimedJob job, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, (session, ct)
            => FenceAsync(Sql(session.DbContext), session.Connection, session.Transaction!.GetDbTransaction(), job, leaseDuration, ct, commandTimeoutSeconds: 5),
            cancellationToken);
    }

    public async Task<bool> ReportProgressAsync(ClaimedJob job, int processed, int? total, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbTransaction transaction = session.Transaction!.GetDbTransaction();
            FenceResult fence = await FenceAsync(sql, session.Connection, transaction, job, leaseDuration, ct);
            if (!fence.Matched)
            {
                return false;
            }

            await JobCommand.Create(session.Connection, transaction, sql.UpdateProgress)
                .Id(job.Id)
                .With("progress_processed", processed, DbType.Int32)
                .With("progress_total", total, DbType.Int32)
                .RunAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<OutcomeResult> RecordOutcomeAsync(ClaimedJob job, AttemptOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outcome);
        return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection connection = session.Connection;
            DbTransaction transaction = session.Transaction!.GetDbTransaction();

            if (await LockLeasedJobAsync(sql, connection, transaction, job, ct) is not { } state)
            {
                return OutcomeResult.LeaseLost;
            }

            return await ApplyOutcomeAsync(sql, connection, transaction, job, state, outcome, ct);
        }, cancellationToken);
    }

    private async Task<OutcomeResult> ApplyOutcomeAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, LockedJob state, AttemptOutcome outcome, CancellationToken ct)
    {
        OutcomeWrite write = new(sql, connection, transaction, job, state, outcome, ct);
        return outcome.Kind switch
        {
            AttemptOutcomeKind.Succeeded => await SucceedAsync(write),
            AttemptOutcomeKind.Cancelled => await CancelAsync(write),
            AttemptOutcomeKind.DeadLetter => await DeadLetterOutcomeAsync(write),
            AttemptOutcomeKind.Failed or AttemptOutcomeKind.TimedOut or AttemptOutcomeKind.LeaseExpired => await FailAsync(write),
            AttemptOutcomeKind.Snoozed => await SnoozeAsync(write),
            AttemptOutcomeKind.Released or AttemptOutcomeKind.Deferred => await ReleaseAsync(write),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Kind, "Unknown outcome."),
        };
    }

    private static async Task<OutcomeResult> SucceedAsync(OutcomeWrite w)
    {
        await EndAsync(w.Sql, w.Connection, w.Transaction, w.Job, JobStatus.Succeeded, JobTerminalReason.Completed, JobAttemptOutcome.Succeeded, refund: false, w.Outcome,
            new JobEvent(JobEventCodes.JobSucceeded), w.Ct);
        if (w.CancelPending)
        {
            // Success wins a race with cancel (§14.4, §15.7).
            await InsertEventAsync(w.Sql, w.Connection, w.Transaction, w.Job.Id, JobEventCodes.JobCancelTooLate, w.Job.WorkerId, w.State.CancelRequestedBy, w.State.CancelReason, w.Ct);
        }

        if (w.Outcome.EventCode is { } extra)
        {
            await InsertEventAsync(w.Sql, w.Connection, w.Transaction, w.Job.Id, extra, w.Job.WorkerId, null, null, w.Ct);
        }

        return OutcomeResult.Recorded;
    }

    private static async Task<OutcomeResult> CancelAsync(OutcomeWrite w)
    {
        await CancelLeasedAsync(w.Sql, w.Connection, w.Transaction, w.Job, w.State, w.Outcome.TerminalReason ?? JobTerminalReason.HandlerCancelled, w.Ct, w.Outcome.Error);
        return OutcomeResult.Recorded;
    }

    private static async Task<OutcomeResult> DeadLetterOutcomeAsync(OutcomeWrite w)
    {
        await DeadLetterAsync(w.Sql, w.Connection, w.Transaction, w.Job, w.Outcome.TerminalReason ?? JobTerminalReason.PermanentError, JobAttemptOutcome.Failed, w.Outcome, w.Ct);
        return OutcomeResult.Recorded;
    }

    private async Task<OutcomeResult> FailAsync(OutcomeWrite w)
    {
        JobAttemptOutcome attemptOutcome = w.Outcome.Kind switch
        {
            AttemptOutcomeKind.TimedOut => JobAttemptOutcome.TimedOut,
            AttemptOutcomeKind.LeaseExpired => JobAttemptOutcome.LeaseLost,
            _ => JobAttemptOutcome.Failed,
        };
        if (w.CancelPending)
        {
            // A job with a pending cancel is never rescheduled (§14.4 step 5).
            await CancelLeasedAsync(w.Sql, w.Connection, w.Transaction, w.Job, w.State, JobTerminalReason.AdminCancelled, w.Ct, w.Outcome.Error, refund: false, attemptOutcome);
            return OutcomeResult.Cancelled;
        }

        bool tooOld = TooOld(w.State);
        if (w.AttemptsLeft <= 0 || tooOld)
        {
            await DeadLetterAsync(w.Sql, w.Connection, w.Transaction, w.Job, tooOld ? JobTerminalReason.MaxAgeExceeded : JobTerminalReason.Exhausted, attemptOutcome, w.Outcome, w.Ct);
            return OutcomeResult.DeadLettered;
        }

        string code = w.Outcome.Kind switch
        {
            AttemptOutcomeKind.TimedOut => JobEventCodes.JobTimedOut,
            AttemptOutcomeKind.LeaseExpired => JobEventCodes.JobLeaseExpired,
            _ => JobEventCodes.JobRetryScheduled,
        };
        await RescheduleAsync(w.Sql, w.Connection, w.Transaction, w.Job, JobStatus.Scheduled, w.Outcome.Delay ?? TimeSpan.Zero, refund: false, snoozed: 0, released: 0, attemptOutcome, w.Outcome,
            new JobEvent(code, Reason: w.Outcome.Error), w.Ct);
        return OutcomeResult.Recorded;
    }

    private async Task<OutcomeResult> SnoozeAsync(OutcomeWrite w)
    {
        if (w.CancelPending)
        {
            await CancelLeasedAsync(w.Sql, w.Connection, w.Transaction, w.Job, w.State, JobTerminalReason.AdminCancelled, w.Ct);
            return OutcomeResult.Cancelled;
        }

        if (w.State.SnoozeCount >= _options.Retry.MaxSnoozes || TooOld(w.State))
        {
            // Over the cap: the snooze counts as a failure (§14.1).
            return await FailAsync(w with { Outcome = w.Outcome with { Kind = AttemptOutcomeKind.Failed, ErrorCode = "SnoozeLimit", Error = "Snooze limit or job age exceeded." } });
        }

        await RescheduleAsync(w.Sql, w.Connection, w.Transaction, w.Job, JobStatus.Scheduled, w.Outcome.Delay ?? TimeSpan.Zero, refund: true, snoozed: 1, released: 0, JobAttemptOutcome.Snoozed, w.Outcome,
            new JobEvent(JobEventCodes.JobSnoozed), w.Ct);
        return OutcomeResult.Recorded;
    }

    private async Task<OutcomeResult> ReleaseAsync(OutcomeWrite w)
    {
        if (w.CancelPending)
        {
            await CancelLeasedAsync(w.Sql, w.Connection, w.Transaction, w.Job, w.State, JobTerminalReason.AdminCancelled, w.Ct);
            return OutcomeResult.Cancelled;
        }

        bool deferred = w.Outcome.Kind == AttemptOutcomeKind.Deferred;
        JobAttemptOutcome attemptOutcome = deferred ? JobAttemptOutcome.Deferred : JobAttemptOutcome.Released;

        // Over MaxReleases a release is charged (§14.1): the job keeps outliving deploys.
        bool refund = w.State.ReleaseCount < _options.Retry.MaxReleases;
        if (!refund && w.AttemptsLeft <= 0)
        {
            await DeadLetterAsync(w.Sql, w.Connection, w.Transaction, w.Job, JobTerminalReason.Exhausted, attemptOutcome, w.Outcome, w.Ct);
            return OutcomeResult.DeadLettered;
        }

        await RescheduleAsync(w.Sql, w.Connection, w.Transaction, w.Job, JobStatus.Enqueued, deferred ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, refund, snoozed: 0, released: 1, attemptOutcome, w.Outcome,
            new JobEvent(deferred ? JobEventCodes.JobDeferred : JobEventCodes.JobReleased), w.Ct);
        return OutcomeResult.Recorded;
    }

    // Database clock (§14.2): the job's age is measured by the server that stamped created_at_utc, never the app clock.
    private bool TooOld(LockedJob state) => state.AgeMs > _options.Retry.MaxJobAge.TotalMilliseconds;

    private sealed record OutcomeWrite(
        JobSql Sql, DbConnection Connection, DbTransaction Transaction, ClaimedJob Job, LockedJob State, AttemptOutcome Outcome, CancellationToken Ct)
    {
        public bool CancelPending => State.CancelRequestedAtUtc is not null;

        public int AttemptsLeft => State.MaxAttempts - State.Attempt;
    }

    private static async Task DeadLetterAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, JobTerminalReason reason, JobAttemptOutcome attemptOutcome, AttemptOutcome outcome, CancellationToken ct)
    {
        await EndAsync(sql, connection, transaction, job, JobStatus.DeadLettered, reason, attemptOutcome, refund: false, outcome,
            new JobEvent(reason == JobTerminalReason.MaxAgeExceeded ? JobEventCodes.JobMaxAgeExceeded : JobEventCodes.JobDeadLettered, Reason: outcome.Error), ct);
    }

    private static async Task CancelLeasedAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, LockedJob state, JobTerminalReason reason, CancellationToken ct,
        string? error = null, bool refund = true, JobAttemptOutcome attemptOutcome = JobAttemptOutcome.Cancelled)
    {
        await EndAsync(sql, connection, transaction, job, JobStatus.Cancelled, reason, attemptOutcome, refund, new AttemptOutcome(AttemptOutcomeKind.Cancelled, Error: error),
            new JobEvent(JobEventCodes.JobCancelled, state.CancelRequestedBy, state.CancelReason ?? error), ct);
    }

    /// <summary>The lifecycle event an outcome write records in the same round trip (worker id: the job's lease owner).</summary>
    private readonly record struct JobEvent(string Code, Guid? ActorId = null, string? Reason = null);

    private static async Task EndAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, JobStatus status, JobTerminalReason reason,
        JobAttemptOutcome attemptOutcome, bool refund, AttemptOutcome outcome, JobEvent jobEvent, CancellationToken ct)
    {
        // One round trip: queue row, job, attempt, active key and the event (JobSql.EndLeased).
        object? ended = await OutcomeCommand(connection, transaction, sql.EndLeased, job, attemptOutcome, refund, outcome, jobEvent)
            .With("status", (short)status, DbType.Int16)
            .With("terminal_reason", (short)reason, DbType.Int16)
            .ScalarAsync(ct);
        if (ended is null or DBNull || Convert.ToInt64(ended, System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException($"Job {job.JobId}: the queue row disappeared while its lease was held.");
        }

        await ReleaseContinuationsAsync(sql, connection, transaction, job.Id, status, ct);
    }

    private static Task<int> RescheduleAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, JobStatus status, TimeSpan delay, bool refund, int snoozed, int released,
        JobAttemptOutcome attemptOutcome, AttemptOutcome outcome, JobEvent jobEvent, CancellationToken ct)
        => OutcomeCommand(connection, transaction, sql.RescheduleLeased, job, attemptOutcome, refund, outcome, jobEvent)
            .With("status", (short)status, DbType.Int16)
            .With("snoozed", snoozed, DbType.Int32)
            .With("released", released, DbType.Int32)
            .With("delay_ms", (long)Math.Max(0, delay.TotalMilliseconds), DbType.Int64)
            .RunAsync(ct);

    /// <summary>The parameters <see cref="JobSql.EndLeased"/> and <see cref="JobSql.RescheduleLeased"/> share.</summary>
    private static DbCommand OutcomeCommand(
        DbConnection connection, DbTransaction transaction, string text, ClaimedJob job, JobAttemptOutcome attemptOutcome, bool refund, AttemptOutcome outcome, JobEvent jobEvent)
        => JobCommand.Create(connection, transaction, text)
            .Id(job.Id)
            .Token(job.LeaseToken)
            .With("refund", refund, DbType.Boolean)
            .With("error_code", Truncate(outcome.ErrorCode, 100), DbType.String)
            .With("error", Truncate(outcome.Error, 2000), DbType.String)
            .With("attempt_seq", job.AttemptSeq, DbType.Int32)
            .With("outcome", (short)attemptOutcome, DbType.Int16)
            .With("exception_type", Truncate(outcome.ExceptionType, 500), DbType.String)
            .With("event_code", jobEvent.Code, DbType.String)
            .With("worker_id", job.WorkerId, DbType.String)
            .With("actor_id", jobEvent.ActorId, DbType.Guid)
            .With("reason", Truncate(jobEvent.Reason, 1000), DbType.String);

    private static Task<int> CloseAttemptAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, JobAttemptOutcome outcome, bool refund,
        string? errorCode, string? error, string? exceptionType, CancellationToken ct)
        => JobCommand.Create(connection, transaction, sql.CloseAttempt)
            .Id(job.Id)
            .With("attempt_seq", job.AttemptSeq, DbType.Int32)
            .With("outcome", (short)outcome, DbType.Int16)
            .With("refund", refund, DbType.Boolean)
            .With("error_code", Truncate(errorCode, 100), DbType.String)
            .With("error", Truncate(error, 2000), DbType.String)
            .With("exception_type", Truncate(exceptionType, 500), DbType.String)
            .RunAsync(ct);

    private static async Task<FenceResult> FenceAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, TimeSpan leaseDuration, CancellationToken ct, int? commandTimeoutSeconds = null)
    {
        await using DbCommand fence = JobCommand.Create(connection, transaction, sql.Fence, commandTimeoutSeconds)
            .Id(job.Id)
            .Token(job.LeaseToken)
            .With("lease_ms", (int)leaseDuration.TotalMilliseconds, DbType.Int32);
        await using DbDataReader reader = await fence.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return FenceResult.Lost;
        }

        DateTimeOffset expires = reader.ReadUtc(0);
        if (sql.IsPostgres)
        {
            return new FenceResult(true, expires, reader.ReadUtcOrNull(1));
        }

        // SQL Server: the cancel flag is the second result set (present only because the fence matched).
        DateTimeOffset? cancel = null;
        if (await reader.NextResultAsync(ct) && await reader.ReadAsync(ct))
        {
            cancel = reader.ReadUtcOrNull(0);
        }

        return new FenceResult(true, expires, cancel);
    }

    /// <summary>The lease check and the job row lock in one round trip; null when the lease is lost.</summary>
    private static async Task<LockedJob?> LockLeasedJobAsync(JobSql sql, DbConnection connection, DbTransaction transaction, ClaimedJob job, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(connection, transaction, sql.LockLeasedJob).Id(job.Id).Token(job.LeaseToken);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        do
        {
            // SQL Server returns the job row as the batch's only result set, and none at all when the lease is lost.
            if (reader.FieldCount > 0 && await reader.ReadAsync(ct))
            {
                return ReadLockedJob(reader);
            }
        }
        while (await reader.NextResultAsync(ct));

        return null;
    }

    private static async Task<LockedJob> LockJobAsync(JobSql sql, DbConnection connection, DbTransaction transaction, long id, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(connection, transaction, sql.LockJob).Id(id);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"Job {id} does not exist.");
        }

        return ReadLockedJob(reader);
    }

    private static LockedJob ReadLockedJob(DbDataReader reader)
        => new(
            (JobStatus)Convert.ToInt16(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.ReadInt(1), reader.ReadInt(2), reader.ReadUtcOrNull(3), reader.ReadUtc(4), reader.ReadInt(5), reader.ReadInt(6),
            reader.ReadGuidOrNull(7), reader.ReadStringOrNull(8), reader.ReadLong(9));

    private JobSql Sql(DbContext context)
        => _sql ??= context.Database.ProviderName switch
        {
            { } name when name.Contains("Npgsql", StringComparison.Ordinal) => new PostgresJobSql(_options.Schema),
            { } name when name.Contains("SqlServer", StringComparison.Ordinal) => new SqlServerJobSql(_options.Schema),
            var name => throw new NotSupportedException($"NinjaQueue supports SQL Server and PostgreSQL, not '{name}'."),
        };

    private static void EnsureGuardInstalled(DbContext context)
    {
        CoreOptionsExtension? core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();
        if (core?.Interceptors?.Contains(FencedTransactionGuard.Instance) != true)
        {
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} must register FencedTransactionGuard (optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance)) before fenced jobs can run.");
        }
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private sealed record LockedJob(
        JobStatus Status, int Attempt, int MaxAttempts, DateTimeOffset? CancelRequestedAtUtc, DateTimeOffset CreatedAtUtc, int SnoozeCount, int ReleaseCount,
        Guid? CancelRequestedBy, string? CancelReason, long AgeMs);

#pragma warning disable CA1032, RCS1194 // Private control-flow exceptions: only the constructors used here.
    private sealed class FencedStepAbortException(FencedStepStatus? status) : Exception("The fenced step could not commit.")
    {
        public FencedStepStatus? Status { get; } = status;
    }

    private sealed class FencedStepFailedException(Exception inner) : Exception("The fenced step's handler failed.", inner);
#pragma warning restore CA1032, RCS1194
}

/// <summary>Fault injection for the G4 tests: runs after a step's finalization, before its COMMIT. Unset in production.</summary>
internal static class FencedStepTestHooks
{
    internal static readonly AsyncLocal<Func<Task>?> BeforeCommit = new();

    internal static Task BeforeCommitAsync() => BeforeCommit.Value?.Invoke() ?? Task.CompletedTask;
}