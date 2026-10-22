using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.Admin;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// Admin storage (§14.4, §15.11, §17.8, §17.9, Release 2). Lock order everywhere: the queue row before the job row, as in
// the claim and outcome transactions, so an admin action never deadlocks with a worker.
internal sealed partial class EfCoreJobStore<TContext> : IJobAdministrationStore
{
    private const int MaxDetailEvents = 500;

    /// <summary>A synchronous bulk operation counts as active for this long (it handles at most 500 short transactions).</summary>
    private static readonly TimeSpan SyncBulkWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    // ---- cancel (§14.4) ----------------------------------------------------------------------------------------------

    public Task<AdminCancelResult> CancelJobAsync(
        JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, Guid? actorId, string reason, bool allowRunning, Func<string, bool> isInterruptible,
        Guid? bulkOperationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(isInterruptible);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await FindInScopeAsync(sql, c, t, jobId, tenantScopes, ct) is not { } id)
            {
                return new AdminCancelResult(CancelResult.NotFound, null);
            }

            // 1. The unleased queue row first (same lock order as claims): 1 row = nobody can claim it any more.
            int dequeued = await JobCommand.Create(c, t, sql.DeleteUnleasedQueueRow).Id(id).RunAsync(ct);

            // 2. The job row under its lock: its status is now exact (an Awaiting child cannot be released meanwhile).
            AdminLockedJob state = await LockForAdminAsync(sql, c, t, id, ct);
            if (state.Status.IsTerminal())
            {
                await InsertEventAsync(sql, c, t, id, JobEventCodes.JobCancelTooLate, null, actorId, reason, ct);
                return new AdminCancelResult(CancelResult.AlreadyCompleted, state.TenantScope);
            }

            if (dequeued == 1 || state.Status == JobStatus.Awaiting)
            {
                await JobCommand.Create(c, t, sql.AdminCancelJob).Id(id)
                    .With("actor_id", actorId, DbType.Guid).With("reason", Truncate(reason, 1000), DbType.String).RunAsync(ct);
                await JobCommand.Create(c, t, sql.DeleteActiveKey).Id(id).RunAsync(ct);
                await InsertEventAsync(sql, c, t, id, JobEventCodes.JobCancelled, null, actorId, reason, ct);
                await BulkEventAsync(sql, c, t, id, bulkOperationId, actorId, reason, ct);

                // §14.4 step 7: continuations are cancelled (or released for OnParentCompletion) in the same transaction.
                await ReleaseContinuationsAsync(sql, c, t, id, JobStatus.Cancelled, ct);
                return new AdminCancelResult(CancelResult.Cancelled, state.TenantScope);
            }

            if (!allowRunning)
            {
                return new AdminCancelResult(CancelResult.RunningNotSupported, state.TenantScope);
            }

            if (await JobCommand.Create(c, t, sql.RequestCancel).Id(id)
                    .With("actor_id", actorId, DbType.Guid).With("reason", Truncate(reason, 1000), DbType.String).RunAsync(ct) == 1)
            {
                await InsertEventAsync(sql, c, t, id, JobEventCodes.JobCancelRequested, null, actorId, reason, ct);
                await BulkEventAsync(sql, c, t, id, bulkOperationId, actorId, reason, ct);

                // §14.4 step 6: a non-interruptible attempt runs to its end; the cancel only stops further attempts.
                return new AdminCancelResult(isInterruptible(state.JobType) ? CancelResult.CancelRequested : CancelResult.CancelAfterCurrentAttempt, state.TenantScope);
            }

            return new AdminCancelResult(CancelResult.AlreadyRequested, state.TenantScope);
        }, cancellationToken);
    }

    // ---- Retry / Requeue (§15.11) ------------------------------------------------------------------------------------

    public Task<AdminRunResult> RunAgainAsync(
        JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, AdminRunRequest request, Guid? bulkOperationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await FindInScopeAsync(sql, c, t, jobId, tenantScopes, ct) is not { } id)
            {
                return new AdminRunResult(AdminRunOutcome.NotFound);
            }

            // A terminal job has no queue row, so the job row is the first lock.
            AdminLockedJob state = await LockForAdminAsync(sql, c, t, id, ct);
            if (!AdminRules.IsEligible(request.Mode, state.Status))
            {
                return new AdminRunResult(AdminRunOutcome.NotEligible, state.Status, TenantScope: state.TenantScope);
            }

            if (AdminRules.NewMaxAttempts(request.Mode, state.Attempt, request.DefaultAttempts(state.JobType)) is not { } maxAttempts)
            {
                return new AdminRunResult(AdminRunOutcome.AttemptLimitReached, state.Status, TenantScope: state.TenantScope);
            }

            if (state.UniqueKey is not null && await ReserveUniqueAsync(sql, c, t, id, state.TenantScope, state.UniqueKey, ct) is { } holder)
            {
                return new AdminRunResult(AdminRunOutcome.Conflict, state.Status, new JobId(holder), TenantScope: state.TenantScope);
            }

            await JobCommand.Create(c, t, sql.RunAgainJob).Id(id)
                .With("max_attempts", maxAttempts, DbType.Int32)
                .With("run_at", request.Mode == AdminRunMode.Requeue ? request.RunAtUtc : null, DbType.DateTimeOffset)
                .With("reset_counters", request.Mode == AdminRunMode.Requeue ? 1 : 0, DbType.Int32)
                .With("expected_status", (short)state.Status, DbType.Int16)
                .RunAsync(ct);
            await ExecAsync(c, t, sql.InsertQueueRows(id.ToString(CultureInfo.InvariantCulture)), ct);
            string mode = request.Mode == AdminRunMode.Retry ? "retry" : "requeue";
            await InsertEventAsync(sql, c, t, id, JobEventCodes.JobRequeued, null, request.ActorId, request.Reason, ct,
                JsonSerializer.Serialize(new { mode, from = state.Status.ToString(), maxAttempts }));
            await BulkEventAsync(sql, c, t, id, bulkOperationId, request.ActorId, request.Reason, ct);

            (int restored, int conflicts) = request.IncludeContinuations
                ? await RestoreContinuationsAsync(sql, c, t, id, request.ActorId, request.Reason, ct)
                : (0, 0);
            JobStatus now = (JobStatus)Convert.ToInt16(await JobCommand.Create(c, t, $"SELECT status FROM {sql.Schema}.background_jobs WHERE id = @id").Id(id).ScalarAsync(ct),
                CultureInfo.InvariantCulture);
            return new AdminRunResult(AdminRunOutcome.Applied, now, null, restored, conflicts, state.TenantScope);
        }, cancellationToken);
    }

    /// <summary>Puts children cancelled because of the parent back to Awaiting, down the chain, re-reserving unique keys (§15.6).</summary>
    private static async Task<(int Restored, int Conflicts)> RestoreContinuationsAsync(
        JobSql sql, DbConnection c, DbTransaction t, long parentId, Guid? actorId, string reason, CancellationToken ct)
    {
        int restored = 0;
        int conflicts = 0;
        Queue<long> parents = new([parentId]);
        while (parents.TryDequeue(out long parent))
        {
            List<(long Id, Guid Scope, string? UniqueKey)> children = [];
            await using (DbCommand select = JobCommand.Create(c, t, sql.RestorableChildren).With("parent_id", parent, DbType.Int64))
            await using (DbDataReader reader = await select.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    children.Add((reader.ReadLong(0), reader.GetGuid(1), reader.ReadStringOrNull(2)));
                }
            }

            foreach ((long child, Guid scope, string? uniqueKey) in children)
            {
                if (uniqueKey is not null && await ReserveUniqueAsync(sql, c, t, child, scope, uniqueKey, ct) is { } holder)
                {
                    conflicts++;
                    await InsertEventAsync(sql, c, t, child, JobEventCodes.ContinuationRestoreConflict, null, actorId, reason, ct,
                        JsonSerializer.Serialize(new { uniqueKey, holder }));
                    continue;
                }

                int restoredRow;
                await using (DbCommand restore = JobCommand.Create(c, t, sql.RestoreChild).Id(child))
                {
                    restoredRow = await restore.ExecuteNonQueryAsync(ct);
                }

                if (restoredRow == 1)
                {
                    restored++;
                    await InsertEventAsync(sql, c, t, child, JobEventCodes.JobRequeued, null, actorId, reason, ct,
                        JsonSerializer.Serialize(new { mode = "continuation-restore" }));
                    parents.Enqueue(child);
                }
            }
        }

        return (restored, conflicts);
    }

    /// <summary>Reserves a unique key for <paramref name="id"/>; returns the holder's job id on conflict (§15.3).</summary>
    private static async Task<Guid?> ReserveUniqueAsync(JobSql sql, DbConnection c, DbTransaction t, long id, Guid scope, string key, CancellationToken ct)
    {
        DbCommand Keyed(string text) => JobCommand.Create(c, t, text).With("tenant_scope", scope, DbType.Guid).With("unique_key", key, DbType.String);
        if (sql.UniqueProbe is { } probe && await ScalarGuidAsync(Keyed(probe), ct) is { } held)
        {
            return held;
        }

        object? reserved;
        await using (DbCommand reserve = Keyed(sql.ReserveUnique).Id(id))
        {
            reserved = await reserve.ExecuteScalarAsync(ct);
        }

        return reserved is null or DBNull
            ? await ScalarGuidAsync(Keyed(sql.UniqueHolder), ct) ?? throw new InvalidOperationException("Unique-key conflict reported, but the holder was not found.")
            : null;
    }

    // ---- reads (§16.8, §17.8) ----------------------------------------------------------------------------------------

    public Task<JobDetail?> GetJobAsync(JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken)
        => unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            long id;
            JobSummary summary;
            string? payload;
            await using (DbCommand detail = JobCommand.Create(c, null, sql.JobDetail).With("guid_id", jobId.Value, DbType.Guid))
            await using (DbDataReader reader = await detail.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct) || !InScope(tenantScopes, reader.GetGuid(24)))
                {
                    return null;
                }

                id = reader.ReadLong(0);
                summary = ReadSummary(reader);
                payload = reader.ReadStringOrNull(25);
            }

            List<JobAttemptInfo> attempts = [];
            await using (DbCommand cmd = JobCommand.Create(c, null, sql.JobAttempts).Id(id))
            await using (DbDataReader r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    attempts.Add(new JobAttemptInfo(r.ReadInt(0), Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture), r.GetString(2), r.ReadUtc(3),
                        r.ReadUtcOrNull(4), r.IsDBNull(5) ? null : r.ReadLong(5),
                        r.IsDBNull(6) ? null : (JobAttemptOutcome)Convert.ToInt16(r.GetValue(6), CultureInfo.InvariantCulture), r.ReadStringOrNull(7), r.ReadStringOrNull(8)));
                }
            }

            List<JobEventInfo> events = [];
            await using (DbCommand cmd = JobCommand.Create(c, null, sql.JobEvents).Id(id).With("take", MaxDetailEvents, DbType.Int32))
            await using (DbDataReader r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    events.Add(new JobEventInfo(r.GetString(0), r.ReadUtc(1), r.ReadStringOrNull(2), r.ReadGuidOrNull(3), r.ReadStringOrNull(4), r.ReadStringOrNull(5)));
                }
            }

            List<JobSideEffectInfo> sideEffects = [];
            await using (DbCommand cmd = JobCommand.Create(c, null, sql.JobSideEffects).Id(id))
            await using (DbDataReader r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    sideEffects.Add(new JobSideEffectInfo(r.GetString(0), (SideEffectStatus)Convert.ToInt16(r.GetValue(1), CultureInfo.InvariantCulture), r.ReadInt(2),
                        r.ReadStringOrNull(3), r.ReadUtc(4), r.ReadUtcOrNull(5)));
                }
            }

            return (JobDetail?)new JobDetail(summary, payload, attempts, events, sideEffects);
        }, cancellationToken);

    public Task<(IReadOnlyList<JobSummary> Items, long? LastId)> ListJobsAsync(
        JobQuery query, long? afterId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            await using DbCommand command = JobCommand.Create(session.Connection, null, "");
            StringBuilder where = new("1 = 1");
            AddScopes(command, where, tenantScopes);
            if (query.Statuses is { Count: > 0 } statuses)
            {
                where.Append(" AND b.status IN (").AppendJoin(", ", statuses.Select(s => ((short)s).ToString(CultureInfo.InvariantCulture))).Append(')');
            }

            Filter(command, where, "b.job_type", "job_type", query.JobType, DbType.String);
            Filter(command, where, "b.queue", "queue", query.Queue, DbType.String);
            Filter(command, where, "b.tenant_id", "tenant_id", query.TenantId, DbType.Guid);
            Filter(command, where, "p.guid_id", "parent_guid", query.ParentJobId?.Value, DbType.Guid);
            Range(command, where, query.CreatedFromUtc, query.CreatedToUtc);
            if (afterId is { } after)
            {
                where.Append(" AND b.id < @after_id");
                command.With("after_id", after, DbType.Int64);
            }

            command.CommandText = sql.ListJobs(where.ToString());
            command.With("take", query.PageSize, DbType.Int32);
            List<JobSummary> items = [];
            long? lastId = null;
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                lastId = reader.ReadLong(0);
                items.Add(ReadSummary(reader));
            }

            return ((IReadOnlyList<JobSummary>)items, items.Count == query.PageSize ? lastId : null);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<(long Id, JobId JobId)>> FindBulkJobsAsync(
        BulkJobFilter filter, IReadOnlyCollection<Guid>? tenantScopes, long afterId, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            await using DbCommand command = JobCommand.Create(session.Connection, null, "");
            StringBuilder where = new("b.id > @after_id");
            command.With("after_id", afterId, DbType.Int64);
            AddScopes(command, where, tenantScopes);
            Filter(command, where, "b.status", "status", filter.Status is { } s ? (short)s : null, DbType.Int16);
            Filter(command, where, "b.job_type", "job_type", filter.JobType, DbType.String);
            Filter(command, where, "b.queue", "queue", filter.Queue, DbType.String);
            Filter(command, where, "b.tenant_id", "tenant_id", filter.TenantId, DbType.Guid);
            Range(command, where, filter.FromUtc, filter.ToUtc);
            command.CommandText = sql.FindBulkJobs(where.ToString());
            command.With("take", take, DbType.Int32);
            List<(long, JobId)> ids = [];
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add((reader.ReadLong(0), new JobId(reader.GetGuid(1))));
            }

            return (IReadOnlyList<(long, JobId)>)ids;
        }, cancellationToken);
    }

    // ---- stats and workers (§16.8) -----------------------------------------------------------------------------------

    public Task<JobStats> GetStatsAsync(IReadOnlyCollection<Guid>? tenantScopes, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
        => unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            List<QueueStats> queues = [];
            await using (DbCommand command = JobCommand.Create(c, null, ""))
            {
                StringBuilder where = new("1 = 1");
                AddScopes(command, where, tenantScopes, "q.tenant_scope");
                command.CommandText = sql.QueueStats(where.ToString());
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    queues.Add(new QueueStats(reader.GetString(0), reader.ReadInt(1), reader.ReadInt(2), reader.ReadInt(3),
                        reader.IsDBNull(4) ? 0 : Math.Max(0, reader.ReadLong(4)) / 1000d));
                }
            }

            int awaiting;
            await using (DbCommand command = JobCommand.Create(c, null, ""))
            {
                StringBuilder where = new("1 = 1");
                AddScopes(command, where, tenantScopes);
                command.CommandText = sql.AwaitingCount(where.ToString());
                awaiting = Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            Dictionary<JobStatus, int> ended = [];
            await using (DbCommand command = JobCommand.Create(c, null, ""))
            {
                StringBuilder where = new("1 = 1");
                AddScopes(command, where, tenantScopes);
                command.CommandText = sql.TerminalCounts(where.ToString());
                command.With("since", sinceUtc, DbType.DateTimeOffset);
                await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ended[(JobStatus)Convert.ToInt16(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.ReadInt(1);
                }
            }

            return new JobStats(queues, awaiting, ended.GetValueOrDefault(JobStatus.Succeeded), ended.GetValueOrDefault(JobStatus.Cancelled),
                ended.GetValueOrDefault(JobStatus.DeadLettered), sinceUtc);
        }, cancellationToken);

    public Task<IReadOnlyList<JobWorkerInfo>> GetWorkersAsync(TimeSpan livenessWindow, CancellationToken cancellationToken)
        => unitOfWork.UseAsync<IReadOnlyList<JobWorkerInfo>>(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            await using DbCommand command = JobCommand.Create(session.Connection, null, sql.LiveWorkers).With("window_ms", -(long)livenessWindow.TotalMilliseconds, DbType.Int64);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            List<JobWorkerInfo> workers = [];
            while (await reader.ReadAsync(ct))
            {
                workers.Add(new JobWorkerInfo(reader.GetString(0), reader.GetString(1), reader.ReadInt(2), reader.ReadStringOrNull(3),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [], JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                    reader.ReadUtc(6), reader.ReadUtc(7)));
            }

            return workers;
        }, cancellationToken);

    // ---- bulk operations (§17.9) -------------------------------------------------------------------------------------

    public async Task<BulkStart> StartBulkOperationAsync(BulkOperationRow operation, NewJob? job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                DbConnection c = session.Connection;
                DbTransaction t = session.Transaction!.GetDbTransaction();

                // Serializes starts of one tenant, so "one active operation per tenant" and the idempotency check are exact.
                await JobCommand.Create(c, t, sql.BulkTenantLock)
                    .With("lock_key", TenantLockKey(operation.TenantScope), DbType.Int64)
                    .With("lock_resource", $"ninjaqueue:bulk-admin:{operation.TenantScope:N}", DbType.String)
                    .RunAsync(ct);

                await using (DbCommand find = JobCommand.Create(c, t, sql.FindBulkByIdempotency)
                    .With("tenant_scope", operation.TenantScope, DbType.Guid)
                    .With("actor_id", operation.ActorId, DbType.Guid)
                    .With("idempotency_key", operation.IdempotencyKey, DbType.String))
                await using (DbDataReader reader = await find.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        return new BulkStart(BulkSubmissionKind.Replayed, ReadOperation(reader));
                    }
                }

                bool active = Convert.ToInt32(await JobCommand.Create(c, t, sql.ActiveBulkOperation)
                    .With("tenant_scope", operation.TenantScope, DbType.Guid)
                    .With("window_ms", -(long)SyncBulkWindow.TotalMilliseconds, DbType.Int64)
                    .With("system_scope", JobLimits.SystemTenantScope, DbType.Guid)
                    .With("bulk_key", BulkUniqueKey(operation.TenantScope), DbType.String)
                    .ScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
                if (active)
                {
                    return new BulkStart(BulkSubmissionKind.RejectedActiveOperation, null);
                }

                await JobCommand.Create(c, t, sql.InsertBulkOperation)
                    .With("guid_id", operation.OperationId, DbType.Guid)
                    .With("tenant_scope", operation.TenantScope, DbType.Guid)
                    .With("operation", (short)operation.Operation, DbType.Int16)
                    .With("actor_id", operation.ActorId, DbType.Guid)
                    .With("reason", Truncate(operation.Reason, 1000), DbType.String)
                    .With("idempotency_key", operation.IdempotencyKey, DbType.String)
                    .With("filter_json", operation.StateJson, DbType.String)
                    .With("status", (short)(operation.Async ? AdminOperationStatus.Pending : AdminOperationStatus.Running), DbType.Int16)
                    .With("requested", operation.Requested, DbType.Int32)
                    .RunAsync(ct);
                if (job is not null)
                {
                    await InsertAsync(sql, c, t, job, ct);
                }

                // Completed here means "started synchronously"; the service applies the ids and stores the report.
                return new BulkStart(operation.Async ? BulkSubmissionKind.Queued : BulkSubmissionKind.Completed, null);
            }, cancellationToken);
        }
        catch (JobConflictException)
        {
            // The bulk job's unique key (bulk-admin:{tenant}) is held: another filter operation is still running.
            return new BulkStart(BulkSubmissionKind.RejectedActiveOperation, null);
        }
    }

    public Task SaveBulkReportAsync(Guid operationId, BulkOperationReport report, string? state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        return AutocommitAsync((sql, c, ct) => JobCommand.Create(c, null, sql.SaveBulkReport)
            .With("guid_id", operationId, DbType.Guid)
            .With("status", (short)report.Status, DbType.Int16)
            .With("requested", report.Requested, DbType.Int32)
            .With("applied", report.Applied, DbType.Int32)
            .With("not_eligible", report.NotEligible, DbType.Int32)
            .With("conflict", report.Conflict, DbType.Int32)
            .With("not_found", report.NotFound, DbType.Int32)
            .With("failed", report.Failed, DbType.Int32)
            .With("report_json", JsonSerializer.Serialize(report.Items.Select(BulkItemRow.From), ReportJson), DbType.String)
            .With("filter_json", state, DbType.String)
            .RunAsync(ct), cancellationToken);
    }

    public Task<BulkOperationRecord?> GetBulkOperationAsync(Guid operationId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken)
        => unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            await using DbCommand find = JobCommand.Create(session.Connection, null, sql.FindBulkById).With("guid_id", operationId, DbType.Guid);
            await using DbDataReader reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            BulkOperationRecord record = ReadOperation(reader);
            return InScope(tenantScopes, record.TenantScope) ? record : null;
        }, cancellationToken);

    /// <summary>The unique key of the bulk job of a tenant: one active filter operation per tenant (§17.9).</summary>
    internal static string BulkUniqueKey(Guid tenantScope) => $"bulk-admin:{tenantScope:N}";

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static BulkOperationRecord ReadOperation(DbDataReader r)
    {
        IReadOnlyList<BulkItem> items = r.IsDBNull(13)
            ? []
            : [.. (JsonSerializer.Deserialize<List<BulkItemRow>>(r.GetString(13), ReportJson) ?? []).Select(i => i.ToItem())];
        BulkOperationReport report = new(
            r.GetGuid(0), (AdminOperationKind)Convert.ToInt16(r.GetValue(2), CultureInfo.InvariantCulture),
            (AdminOperationStatus)Convert.ToInt16(r.GetValue(6), CultureInfo.InvariantCulture),
            r.ReadInt(7), r.ReadInt(8), r.ReadInt(9), r.ReadInt(10), r.ReadInt(11), r.ReadInt(12), items, r.ReadUtc(14), r.ReadUtcOrNull(15));
        return new BulkOperationRecord(report, r.GetGuid(1), r.GetGuid(3), r.GetString(4), r.ReadStringOrNull(5));
    }

    private static long TenantLockKey(Guid tenantScope)
    {
        Span<byte> bytes = stackalloc byte[16];
        tenantScope.TryWriteBytes(bytes);

        // A fixed salt keeps the advisory key space apart from other users of pg_advisory_xact_lock.
        return BitConverter.ToInt64(bytes[..8]) ^ BitConverter.ToInt64(bytes[8..]) ^ 0x4E494E4A41424C4B;
    }

    private static async Task<long?> FindInScopeAsync(JobSql sql, DbConnection c, DbTransaction t, JobId jobId, IReadOnlyCollection<Guid>? scopes, CancellationToken ct)
    {
        await using DbCommand find = JobCommand.Create(c, t, sql.FindJobForAdmin).With("guid_id", jobId.Value, DbType.Guid);
        await using DbDataReader reader = await find.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && InScope(scopes, reader.GetGuid(2)) ? reader.ReadLong(0) : null;
    }

    private static bool InScope(IReadOnlyCollection<Guid>? scopes, Guid scope) => scopes is null || scopes.Contains(scope);

    private static async Task<AdminLockedJob> LockForAdminAsync(JobSql sql, DbConnection c, DbTransaction t, long id, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, sql.LockJobForAdmin).Id(id);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AdminLockedJob((JobStatus)Convert.ToInt16(reader.GetValue(0), CultureInfo.InvariantCulture), reader.ReadInt(1), reader.GetGuid(2),
                reader.ReadStringOrNull(3), reader.GetString(4))
            : throw new InvalidOperationException($"Job {id} does not exist.");
    }

    private static Task<int> BulkEventAsync(JobSql sql, DbConnection c, DbTransaction t, long id, Guid? operationId, Guid? actorId, string reason, CancellationToken ct)
        => operationId is { } op
            ? InsertEventAsync(sql, c, t, id, JobEventCodes.BulkOperationApplied, null, actorId, reason, ct, JsonSerializer.Serialize(new { operationId = op }))
            : Task.FromResult(0);

    private static JobSummary ReadSummary(DbDataReader r) => new(
        new JobId(r.GetGuid(1)), r.GetString(2), r.GetString(3), r.ReadGuidOrNull(4),
        (JobStatus)Convert.ToInt16(r.GetValue(5), CultureInfo.InvariantCulture),
        r.IsDBNull(6) ? null : (JobTerminalReason)Convert.ToInt16(r.GetValue(6), CultureInfo.InvariantCulture),
        r.ReadInt(7), r.ReadInt(8), r.ReadUtc(9), r.ReadUtc(10), r.ReadUtcOrNull(11), r.ReadUtcOrNull(12), r.ReadStringOrNull(13), r.ReadStringOrNull(14),
        r.ReadGuidOrNull(15) is { } parent ? new JobId(parent) : null, r.ReadStringOrNull(16), r.ReadStringOrNull(17), r.ReadUtcOrNull(18),
        r.IsDBNull(19) ? null : r.ReadInt(19), r.IsDBNull(20) ? null : r.ReadInt(20), r.ReadStringOrNull(21), r.ReadGuidOrNull(22), r.ReadStringOrNull(23));

    private static void AddScopes(DbCommand command, StringBuilder where, IReadOnlyCollection<Guid>? scopes, string column = "b.tenant_scope")
    {
        if (scopes is null)
        {
            return;
        }

        if (scopes.Count == 0)
        {
            where.Append(" AND 1 = 0");
            return;
        }

        where.Append(" AND ").Append(column).Append(" IN (");
        int i = 0;
        foreach (Guid scope in scopes)
        {
            string name = $"scope{i.ToString(CultureInfo.InvariantCulture)}";
            where.Append(i++ == 0 ? "@" : ", @").Append(name);
            command.With(name, scope, DbType.Guid);
        }

        where.Append(')');
    }

    private static void Filter(DbCommand command, StringBuilder where, string column, string name, object? value, DbType type)
    {
        if (value is null)
        {
            return;
        }

        where.Append(" AND ").Append(column).Append(" = @").Append(name);
        command.With(name, value, type);
    }

    private static void Range(DbCommand command, StringBuilder where, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is { } f)
        {
            where.Append(" AND b.created_at_utc >= @created_from");
            command.With("created_from", f, DbType.DateTimeOffset);
        }

        if (to is { } t)
        {
            where.Append(" AND b.created_at_utc < @created_to");
            command.With("created_to", t, DbType.DateTimeOffset);
        }
    }

    private sealed record AdminLockedJob(JobStatus Status, int Attempt, Guid TenantScope, string? UniqueKey, string JobType);

    private sealed record BulkItemRow(Guid JobId, BulkItemOutcome Outcome, JobStatus? Status, Guid? HolderJobId, string? Error)
    {
        public static BulkItemRow From(BulkItem item) => new(item.JobId.Value, item.Outcome, item.Status, item.HolderJobId?.Value, item.Error);

        public BulkItem ToItem() => new(new JobId(JobId), Outcome, Status, HolderJobId is { } h ? new JobId(h) : null, Error);
    }
}