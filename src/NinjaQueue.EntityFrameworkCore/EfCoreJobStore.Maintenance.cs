using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Observability;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

internal sealed partial class EfCoreJobStore<TContext> : IJobMaintenanceStore, IJobAdminStore
{
    // ---- expired-lease recovery (§7.4) -----------------------------------------------------------------------------

    public Task<int> RecoverExpiredLeasesAsync(int batchSize, Func<int, TimeSpan> backoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backoff);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection connection = session.Connection;
            DbTransaction transaction = session.Transaction!.GetDbTransaction();

            List<(long Id, Guid Token)> expired = [];
            await using (DbCommand select = JobCommand.Create(connection, transaction, sql.LockExpiredLeases).With("n", batchSize, DbType.Int32))
            await using (DbDataReader reader = await select.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    expired.Add((reader.ReadLong(0), reader.GetGuid(1)));
                }
            }

            foreach ((long id, Guid token) in expired)
            {
                int seq = Convert.ToInt32(await ScalarAsync(connection, transaction, sql.CurrentAttemptSeq, id, ct), CultureInfo.InvariantCulture);
                ClaimedJob job;
                await using (DbCommand summary = JobCommand.Create(connection, transaction, sql.JobSummary).Id(id))
                await using (DbDataReader reader = await summary.ExecuteReaderAsync(ct))
                {
                    await reader.ReadAsync(ct);
                    job = new ClaimedJob(id, new JobId(reader.GetGuid(0)), reader.GetString(1), reader.ReadGuidOrNull(2), token, "recovery", DateTimeOffset.MinValue,
                        reader.ReadInt(3), seq, reader.ReadInt(4), null, null, null, null, 0, 0, reader.ReadUtc(5));
                }

                // The attempt was charged at claim; a crash-looping job stays bounded by max_attempts (§14.1).
                LockedJob state = await LockJobAsync(sql, connection, transaction, id, ct);
                await ApplyOutcomeAsync(sql, connection, transaction, job, state,
                    new AttemptOutcome(AttemptOutcomeKind.LeaseExpired, backoff(job.Attempt), "LeaseExpired", "The worker's lease expired (crash, hang or lost connection)."), ct);
            }

            return expired.Count;
        }, cancellationToken);
    }

    // ---- workers and leases ----------------------------------------------------------------------------------------

    public Task HeartbeatAsync(WorkerRegistration worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return AutocommitAsync((sql, c, ct) => JobCommand.Create(c, null, sql.UpsertWorker)
            .With("worker_id", worker.WorkerId, DbType.String)
            .With("host", Truncate(worker.Host, 200), DbType.String)
            .With("process_id", worker.ProcessId, DbType.Int32)
            .With("app_version", Truncate(worker.AppVersion, 100), DbType.String)
            .With("queues", Truncate(JsonSerializer.Serialize(worker.Queues), 2000), DbType.String)
            .With("job_types", JsonSerializer.Serialize(worker.JobTypes), DbType.String)
            .RunAsync(ct), cancellationToken);
    }

    public Task RemoveWorkerAsync(string workerId, TimeSpan staleAfter, CancellationToken cancellationToken)
        => AutocommitAsync(async (sql, c, ct) =>
        {
            await JobCommand.Create(c, null, sql.DeleteWorker).With("worker_id", workerId, DbType.String).RunAsync(ct);
            return await JobCommand.Create(c, null, sql.DeleteStaleWorkers).With("stale_ms", -(long)staleAfter.TotalMilliseconds, DbType.Int64).RunAsync(ct);
        }, cancellationToken);

    public async Task<bool> TryAcquireLeaseAsync(string name, string owner, Guid token, TimeSpan ttl, CancellationToken cancellationToken)
        => await AutocommitAsync(async (sql, c, ct) =>
        {
            await JobCommand.Create(c, null, sql.EnsureLease).With("name", name, DbType.String).RunAsync(ct);
            return await JobCommand.Create(c, null, sql.AcquireOrRenewLease)
                .With("name", name, DbType.String)
                .With("owner", Truncate(owner, 200), DbType.String)
                .Token(token)
                .With("ttl_ms", (long)ttl.TotalMilliseconds, DbType.Int64)
                .RunAsync(ct);
        }, cancellationToken) == 1;

    public Task ReleaseLeaseAsync(string name, Guid token, CancellationToken cancellationToken)
        => AutocommitAsync((sql, c, ct) => JobCommand.Create(c, null, sql.ReleaseLease).With("name", name, DbType.String).Token(token).RunAsync(ct), cancellationToken);

    // ---- orphans (§14.3) -------------------------------------------------------------------------------------------

    public Task<int> DetectOrphansAsync(TimeSpan orphanAfter, TimeSpan livenessWindow, CancellationToken cancellationToken)
        => unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();

            HashSet<string> live = [with(StringComparer.Ordinal)];
            await using (DbCommand workers = JobCommand.Create(c, t, sql.LiveWorkerTypes).With("window_ms", -(long)livenessWindow.TotalMilliseconds, DbType.Int64))
            await using (DbDataReader reader = await workers.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    foreach (string type in JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [])
                    {
                        live.Add(type);
                    }
                }
            }

            if (live.Count == 0)
            {
                // No live worker at all (workers disabled, or every instance down): nothing can be judged orphaned.
                return 0;
            }

            int flagged = 0;
            foreach (string type in await ReadStringsAsync(c, t, sql.PendingTypesOlderThan, cmd => cmd.With("age_ms", -(long)orphanAfter.TotalMilliseconds, DbType.Int64), ct))
            {
                if (live.Contains(type))
                {
                    continue;
                }

                // Events only for the jobs flagged now: a type already orphaned earlier must not get a second JobOrphaned per job.
                List<long> ids = await ReadLongsAsync(c, t, sql.FlagOrphans, cmd => cmd.With("job_type", type, DbType.String)
                    .With("age_ms", -(long)orphanAfter.TotalMilliseconds, DbType.Int64), ct);
                flagged += ids.Count;
                foreach (long[] chunk in ids.Chunk(1000))
                {
                    await JobCommand.Create(c, t, sql.OrphanFlaggedEvents(string.Join(",", chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))))
                        .With("event_code", JobEventCodes.JobOrphaned, DbType.String)
                        .With("reason", $"No live worker advertises '{type}'.", DbType.String).RunAsync(ct);
                }
            }

            foreach (string type in await ReadStringsAsync(c, t, sql.OrphanedTypes, _ => { }, ct))
            {
                if (live.Contains(type))
                {
                    await JobCommand.Create(c, t, sql.OrphanEvents).With("job_type", type, DbType.String)
                        .With("event_code", JobEventCodes.JobOrphanCleared, DbType.String)
                        .With("reason", $"A live worker now advertises '{type}'.", DbType.String).RunAsync(ct);
                    await JobCommand.Create(c, t, sql.ClearOrphans).With("job_type", type, DbType.String).RunAsync(ct);
                }
            }

            return flagged;
        }, cancellationToken);

    // ---- purge (§14.7) ---------------------------------------------------------------------------------------------

    /// <summary>How long one purge call may spend on one tier before it moves on (the next round continues).</summary>
    private static readonly TimeSpan PurgeTierBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Both retention tiers (§14.7). Each tier deletes up to <see cref="NinjaQueueRetentionOptions.PurgeBatchSize"/> jobs per
    /// call, in short transactions of at most <see cref="JobSql.PurgeStatementBatch"/> jobs each, and stops early when its
    /// time budget is spent, so no purge transaction holds its locks for long. Returns the rows deleted or slimmed.
    /// </summary>
    public async Task<int> PurgeAsync(NinjaQueueRetentionOptions retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        int affected = 0;
        foreach ((JobStatus status, TimeSpan age) in ((JobStatus, TimeSpan)[])[(JobStatus.Succeeded, retention.Succeeded), (JobStatus.Cancelled, retention.Cancelled), (JobStatus.DeadLettered, retention.DeadLettered)])
        {
            affected += await PurgeTierAsync(retention.PurgeBatchSize, audit: false, sql => sql.OperationalPurgeCandidates, cmd => cmd
                .With("status", (short)status, DbType.Int16).With("age_ms", -(long)age.TotalMilliseconds, DbType.Int64), cancellationToken);
        }

        affected += await PurgeTierAsync(retention.PurgeBatchSize, audit: true, sql => sql.AuditPurgeCandidates, cmd => cmd
            .With("age_ms", -(long)retention.AuditRow.TotalMilliseconds, DbType.Int64), cancellationToken);
        return affected;
    }

    private async Task<int> PurgeTierAsync(
        int maxJobs, bool audit, Func<JobSql, string> candidates, Action<DbCommand> parameters, CancellationToken cancellationToken)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int jobs = 0;
        int rows = 0;
        while (jobs < maxJobs && System.Diagnostics.Stopwatch.GetElapsedTime(started) < PurgeTierBudget)
        {
            int wanted = 0;
            (int found, int deleted) = await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                DbConnection c = session.Connection;
                DbTransaction t = session.Transaction!.GetDbTransaction();
                wanted = Math.Min(sql.PurgeStatementBatch, maxJobs - jobs);
                List<long> ids = await ReadLongsAsync(c, t, candidates(sql), cmd =>
                {
                    parameters(cmd);
                    cmd.With("n", wanted, DbType.Int32);
                }, ct);
                if (ids.Count == 0)
                {
                    return (0, 0);
                }

                // Children before parents, no cascades.
                string list = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                int n = await JobCommand.Create(c, t, sql.PurgeAttempts(list)).RunAsync(ct);
                n += await JobCommand.Create(c, t, sql.PurgeEvents(list)).RunAsync(ct);
                n += await JobCommand.Create(c, t, sql.PurgeSideEffects(list)).RunAsync(ct);
                n += await JobCommand.Create(c, t, audit ? sql.PurgeJobs(list) : sql.SlimJobs(list)).RunAsync(ct);
                return (ids.Count, n);
            }, cancellationToken);

            jobs += found;
            rows += deleted;
            if (found < wanted || found == 0)
            {
                break;
            }
        }

        return rows;
    }

    // ---- watchdog (§16.3) ------------------------------------------------------------------------------------------

    public async Task<int> KillOverdueFencedStepsAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        List<(long JobId, Guid Token, int SessionId, DateTimeOffset SessionStart, long? TransactionId)> overdue = [];
        await AutocommitAsync(async (sql, c, ct) =>
        {
            // Every round proves the session views are readable, even when nothing is overdue (§16.3 watchdog_unreachable).
            await JobCommand.Create(c, null, sql.SessionViewProbe).ScalarAsync(ct);
            await using DbCommand select = JobCommand.Create(c, null, sql.OverdueFencedSteps).With("grace_ms", -(long)grace.TotalMilliseconds, DbType.Int64);
            await using DbDataReader reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                overdue.Add((reader.ReadLong(0), reader.GetGuid(1), reader.ReadInt(2), reader.ReadUtc(3), reader.IsDBNull(4) ? null : reader.ReadLong(4)));
            }

            return overdue.Count;
        }, cancellationToken);

        int killed = 0;
        foreach ((long JobId, Guid Token, int SessionId, DateTimeOffset SessionStart, long? TransactionId) step in overdue)
        {
            killed += await AutocommitAsync(async (sql, c, ct) =>
            {
                // Identity check first: a reused session id, or a connection back in the pool, is never killed.
                async Task<bool> StillOpenAsync() => Convert.ToInt32(await JobCommand.Create(c, null, sql.SessionStillOpen)
                    .With("session_id", step.SessionId, DbType.Int32)
                    .With("session_start", sql.IsPostgres ? step.SessionStart : (object)step.SessionStart.UtcDateTime, sql.IsPostgres ? DbType.DateTimeOffset : DbType.DateTime)
                    .With("transaction_id", step.TransactionId, DbType.Int64)
                    .ScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
                int result = 0;
                if (await StillOpenAsync())
                {
                    await JobCommand.Create(c, null, sql.KillSession(step.SessionId)).RunAsync(ct);

                    // Kill confirmed, not assumed (§16.3): re-read the session view until it is gone (up to 10 s). If it is still
                    // there, keep the registration so the next round checks again, and record nothing yet.
                    bool gone = !await StillOpenAsync();
                    for (int i = 0; i < 100 && !gone; i++)
                    {
                        await Task.Delay(100, ct);
                        gone = !await StillOpenAsync();
                    }

                    if (!gone)
                    {
                        return 0;
                    }

                    result = 1;
                    await InsertEventAsync(sql, c, null, step.JobId, JobEventCodes.FencedStepKilled, "watchdog", null, $"Session {step.SessionId} passed its fenced-step deadline.", ct);
                }

                await JobCommand.Create(c, null, sql.UnregisterFencedStep).Id(step.JobId).Token(step.Token).RunAsync(ct);
                return result;
            }, cancellationToken);
        }

        return killed;
    }

    private async Task RegisterFencedStepAsync(JobSql sql, DbConnection fenced, DbTransaction transaction, ClaimedJob job, TimeSpan stepTimeout, CancellationToken ct)
    {
        int sessionId;
        DateTimeOffset sessionStart;
        long? transactionId;
        await using (DbCommand identity = JobCommand.Create(fenced, transaction, sql.SessionIdentity))
        await using (DbDataReader reader = await identity.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            sessionId = reader.ReadInt(0);
            sessionStart = reader.GetValue(1) is DateTime local ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero) : reader.ReadUtc(1);
            transactionId = reader.IsDBNull(2) ? null : reader.ReadLong(2);
        }

        // Written in autocommit on a separate connection, so other instances (the maintenance leader) can see it. A row left by an
        // earlier lease of this job (keyed by job_id) would otherwise make every retry's registration fail and dead-letter the job.
        await AutocommitAsync(async (s, c, token) =>
        {
            await JobCommand.Create(c, null, s.DropStaleFencedStep).Id(job.Id).Token(job.LeaseToken).RunAsync(token);
            return await JobCommand.Create(c, null, s.RegisterFencedStep)
                .Id(job.Id).Token(job.LeaseToken)
                .With("session_id", sessionId, DbType.Int32)
                .With("session_start", sessionStart, DbType.DateTimeOffset)
                .With("transaction_id", transactionId, DbType.Int64)
                .With("deadline_ms", (long)stepTimeout.TotalMilliseconds, DbType.Int64)
                .With("owner", job.WorkerId, DbType.String)
                .RunAsync(token);
        }, ct);
    }

    private async Task UnregisterFencedStepSafeAsync(ClaimedJob job)
    {
        try
        {
            await AutocommitAsync((sql, c, ct) => JobCommand.Create(c, null, sql.UnregisterFencedStep).Id(job.Id).Token(job.LeaseToken).RunAsync(ct), CancellationToken.None);
        }
        catch (DbException)
        {
            // Best effort: a leftover row is dropped by the next registration of this job and, until then, only makes the
            // watchdog re-check an already-ended session (a no-op).
        }
    }

    // ---- queue samples and server checks ---------------------------------------------------------------------------

    public Task<IReadOnlyList<QueueSample>> SampleQueuesAsync(CancellationToken cancellationToken)
        => unitOfWork.UseAsync<IReadOnlyList<QueueSample>>(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            string text = $"""
                SELECT queue, count(*), {sql.MsSince("MIN(CASE WHEN available_at_utc <= " + sql.Now + " THEN available_at_utc END)")}
                  FROM {sql.Schema}.job_queue WHERE lease_token IS NULL GROUP BY queue
                """;
            List<QueueSample> samples = [];
            await using DbCommand command = JobCommand.Create(session.Connection, null, text);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                long ageMs = reader.IsDBNull(2) ? 0 : Math.Max(0, reader.ReadLong(2));
                samples.Add(new QueueSample(reader.GetString(0), reader.ReadLong(1), TimeSpan.FromMilliseconds(ageMs)));
            }

            return samples;
        }, cancellationToken);

    public Task<ServerCheck> CheckServerAsync(CancellationToken cancellationToken)
        => unitOfWork.UseAsync(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            int version;
            bool rcsi;
            await using (DbCommand checks = JobCommand.Create(session.Connection, null, sql.ServerChecks))
            await using (DbDataReader reader = await checks.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                version = reader.ReadInt(0);
                rcsi = reader.ReadInt(1) == 1;
            }

            bool canKill = Convert.ToInt32(await JobCommand.Create(session.Connection, null, sql.CanKillSessions).ScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
            return new ServerCheck(sql.IsPostgres, version, rcsi, canKill);
        }, cancellationToken);

    // ---- admin cancel (§14.4) --------------------------------------------------------------------------------------

    // One cancel protocol for Release 1 and 2: the admin store's (EfCoreJobStore.Admin.cs), which also cascades continuations.
    public Task<CancelResult> CancelAsync(JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, Guid? actorId, string reason, bool allowRunning, CancellationToken cancellationToken)
        => CancelResultOnlyAsync(CancelJobAsync(jobId, tenantScopes, actorId, reason, allowRunning, _ => true, null, cancellationToken));

    private static async Task<CancelResult> CancelResultOnlyAsync(Task<Admin.AdminCancelResult> cancel) => (await cancel).Result;

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>Runs commands on a fresh session with no transaction (each statement commits on its own).</summary>
    private Task<int> AutocommitAsync(Func<JobSql, DbConnection, CancellationToken, Task<int>> work, CancellationToken cancellationToken)
        => unitOfWork.UseAsync((session, ct) => work(Sql(session.DbContext), session.Connection, ct), cancellationToken);

    private static async Task<object?> ScalarAsync(DbConnection c, DbTransaction t, string text, long id, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, text).Id(id);
        return await command.ExecuteScalarAsync(ct);
    }

    private static async Task<List<string>> ReadStringsAsync(DbConnection c, DbTransaction t, string text, Action<DbCommand> parameters, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, text);
        parameters(command);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        List<string> values = [];
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<List<long>> ReadLongsAsync(DbConnection c, DbTransaction t, string text, Action<DbCommand> parameters, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, text);
        parameters(command);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        List<long> values = [];
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.ReadLong(0));
        }

        return values;
    }
}