using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;
using NinjaQueue.EntityFrameworkCore.Data;

namespace NinjaQueue.EntityFrameworkCore;

// Recurring schedules (§14.6, §15.8): startup sync and the atomic lock-enqueue-advance per due schedule.
internal sealed partial class EfCoreJobStore<TContext> : IJobRecurringStore
{
    // ---- sync (§14.6) ----------------------------------------------------------------------------------------------

    public async Task<RecurringSyncResult> SyncAsync(
        IReadOnlyList<RecurringDefinitionRow> definitions, Func<RecurringDefinitionRow, DateTimeOffset, DateTimeOffset> nextAfter, bool disableRemoved,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(nextAfter);
        int inserted = 0, updated = 0, unchanged = 0, skipped = 0, disabled = 0;
        foreach (RecurringDefinitionRow definition in definitions)
        {
            // One short transaction per schedule: the row (or, on SQL Server, its key range) is locked only while it is decided.
            SyncOutcome outcome = await unitOfWork.UseTransactionAsync(
                IsolationLevel.ReadCommitted, (session, ct) => SyncOneAsync(session, definition, nextAfter, ct), cancellationToken);

            switch (outcome)
            {
                case SyncOutcome.Inserted: inserted++; break;
                case SyncOutcome.Updated: updated++; break;
                case SyncOutcome.Unchanged: unchanged++; break;
                default: skipped++; break;
            }
        }

        if (disableRemoved && definitions.Count > 0)
        {
            disabled = await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                string[] names = [.. definitions.Select((_, i) => $"c{i.ToString(CultureInfo.InvariantCulture)}")];
                DbCommand command = JobCommand.Create(session.Connection, session.Transaction!.GetDbTransaction(), sql.DisableRemovedRecurring(names));
                for (int i = 0; i < names.Length; i++)
                {
                    command.With(names[i], definitions[i].Code, DbType.String);
                }

                return await command.RunAsync(ct);
            }, cancellationToken);
        }

        return new RecurringSyncResult(inserted, updated, unchanged, skipped, disabled);
    }

    private async Task<SyncOutcome> SyncOneAsync(
        JobSession<TContext> session, RecurringDefinitionRow definition, Func<RecurringDefinitionRow, DateTimeOffset, DateTimeOffset> nextAfter, CancellationToken ct)
    {
        JobSql sql = Sql(session.DbContext);
        DbConnection c = session.Connection;
        DbTransaction t = session.Transaction!.GetDbTransaction();
        DateTimeOffset now = await DbNowAsync(sql, c, t, ct);

        StoredSchedule? stored = await LockScheduleAsync(sql, c, t, definition.Code, ct);
        if (stored is null)
        {
            object? id = await DefinitionCommand(c, t, sql.InsertRecurring, definition)
                .With("next_run", nextAfter(definition, now), DbType.DateTimeOffset).ScalarAsync(ct);
            if (id is not null and not DBNull)
            {
                return SyncOutcome.Inserted;
            }

            // PostgreSQL: another instance inserted the code between our lock attempt and insert.
            stored = await LockScheduleAsync(sql, c, t, definition.Code, ct)
                ?? throw new InvalidOperationException($"Recurring schedule '{definition.Code}' conflicted on insert but was not found.");
        }

        if (definition.DefinitionVersion < stored.DefinitionVersion)
        {
            return SyncOutcome.SkippedNewerStored;
        }

        bool scheduleChanged = !string.Equals(stored.Cron, definition.Cron, StringComparison.Ordinal)
            || !string.Equals(stored.TimeZone, definition.TimeZone, StringComparison.Ordinal);
        bool reenable = !stored.Enabled && stored.PausedBy is null;
        bool same = !scheduleChanged && !reenable
            && stored.DefinitionVersion == definition.DefinitionVersion
            && string.Equals(stored.JobType, definition.JobType, StringComparison.Ordinal)
            && string.Equals(stored.Queue, definition.Queue, StringComparison.Ordinal)
            && string.Equals(stored.Payload, definition.Payload, StringComparison.Ordinal)
            && stored.MisfirePolicy == definition.MisfirePolicy
            && stored.AllowOverlap == definition.AllowOverlap;
        if (same)
        {
            return SyncOutcome.Unchanged;
        }

        // A changed cron or zone, or a schedule re-added after removal, restarts from now: no misfire flood for a
        // schedule that did not exist (in this form) during the gap. Otherwise the pending occurrence is kept.
        DateTimeOffset next = scheduleChanged || reenable ? nextAfter(definition, now) : stored.NextRunAtUtc;
        await DefinitionCommand(c, t, sql.UpdateRecurringDefinition, definition)
            .With("enabled", stored.Enabled || reenable, DbType.Boolean)
            .With("next_run", next, DbType.DateTimeOffset)
            .Id(stored.Id)
            .RunAsync(ct);
        return SyncOutcome.Updated;
    }

    private enum SyncOutcome
    {
        Inserted,
        Updated,
        Unchanged,
        SkippedNewerStored,
    }

    private sealed record StoredSchedule(
        long Id, int DefinitionVersion, bool Enabled, Guid? PausedBy, string Cron, string TimeZone, DateTimeOffset NextRunAtUtc, string JobType, string Queue,
        string? Payload, MisfirePolicy MisfirePolicy, bool AllowOverlap);

    private static async Task<StoredSchedule?> LockScheduleAsync(JobSql sql, DbConnection c, DbTransaction t, string code, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, sql.LockRecurringByCode).With("code", code, DbType.String);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new StoredSchedule(
                reader.ReadLong(0), reader.ReadInt(1), reader.GetBoolean(2), reader.ReadGuidOrNull(3), reader.GetString(4), reader.GetString(5),
                reader.ReadUtc(6), reader.GetString(7), reader.GetString(8), reader.ReadStringOrNull(9),
                (MisfirePolicy)Convert.ToInt16(reader.GetValue(10), CultureInfo.InvariantCulture), reader.GetBoolean(11))
            : null;
    }

    private static DbCommand DefinitionCommand(DbConnection c, DbTransaction t, string text, RecurringDefinitionRow definition)
        => JobCommand.Create(c, t, text)
            .With("code", definition.Code, DbType.String)
            .With("job_type", definition.JobType, DbType.String)
            .With("queue", definition.Queue, DbType.String)
            .With("payload", definition.Payload, DbType.String)
            .With("cron", definition.Cron, DbType.String)
            .With("time_zone", definition.TimeZone, DbType.String)
            .With("misfire_policy", (short)definition.MisfirePolicy, DbType.Int16)
            .With("allow_overlap", definition.AllowOverlap, DbType.Boolean)
            .With("definition_version", definition.DefinitionVersion, DbType.Int32);

    private static async Task<DateTimeOffset> DbNowAsync(JobSql sql, DbConnection c, DbTransaction t, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, sql.DatabaseNow);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return reader.ReadUtc(0);
    }

    // ---- firing (§15.8) --------------------------------------------------------------------------------------------

    public async Task<RecurringTickResult> FireDueAsync(int maxSchedules, Func<DueRecurringSchedule, RecurringFire?> plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        int schedules = 0, enqueued = 0, skippedOverlap = 0, deduplicated = 0, failed = 0;
        List<long> excluded = [];
        List<(string Code, Exception Error)> failures = [];
        Dictionary<long, int> attempts = [];
        HashSet<string> queues = [with(StringComparer.Ordinal)];

        // Bounded: each schedule is fired, left (unknown job type) or given up once; deadlock reruns get headroom.
        for (int i = 0; i < maxSchedules * 3 && schedules + excluded.Count < maxSchedules; i++)
        {
            long lockedId = 0;
            string lockedCode = "";
            FireOutcome? outcome;
            try
            {
                outcome = await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
                {
                    JobSql sql = Sql(session.DbContext);
                    DbConnection c = session.Connection;
                    DbTransaction t = session.Transaction!.GetDbTransaction();

                    // 1. Lock one due schedule, skipping rows another scheduler holds.
                    DueRecurringSchedule? due = await LockDueAsync(sql, c, t, excluded, ct);
                    if (due is null)
                    {
                        return null;
                    }

                    lockedId = due.Id;
                    lockedCode = due.Code;

                    // 2. The occurrences per the misfire policy (computed by the engine; null = job type unknown here).
                    RecurringFire? fire = plan(due);
                    if (fire is null)
                    {
                        return new FireOutcome(false, 0, 0, 0);
                    }

                    // 3. Enqueue each occurrence; with AllowOverlap = false the job reserves recurring:{code} (§15.3).
                    int added = 0, overlap = 0, dedup = 0;
                    long? lastJobId = null;
                    foreach (RecurringOccurrenceJob occurrence in fire.Jobs)
                    {
                        JobId result = await InsertAsync(sql, c, t, occurrence.Job, ct);
                        (long jobId, string? idempotencyKey) = await JobRefAsync(sql, c, t, result.Value, ct);
                        if (result.Value == occurrence.Job.GuidId)
                        {
                            added++;
                            lastJobId = jobId;
                        }
                        else if (string.Equals(idempotencyKey, occurrence.Job.IdempotencyKey, StringComparison.Ordinal))
                        {
                            // This occurrence was enqueued before (a double tick): the idempotency key held.
                            dedup++;
                        }
                        else
                        {
                            // Held by the still-active previous occurrence: don't enqueue; record the skip once, on the blocker.
                            overlap++;
                            await InsertEventAsync(sql, c, t, jobId, JobEventCodes.RecurringSkippedOverlap, null, null,
                                $"Recurring schedule '{due.Code}' skipped an occurrence: the previous occurrence is still active.", ct,
                                JsonSerializer.Serialize(new { schedule = due.Code, occurrenceUtc = occurrence.OccurrenceUtc.ToUniversalTime() }));
                        }
                    }

                    if (added > 0 && _options.ListenNotify.Enabled && sql.IsPostgres)
                    {
                        // Delivered on commit, together with the jobs.
                        await JobCommand.Create(c, t, PostgresWakeup.NotifySql)
                            .With("channel", PostgresWakeup.Channel(sql.Schema), DbType.String)
                            .With("queue", due.Queue, DbType.String)
                            .ScalarAsync(ct);
                    }

                    // 4. In every case advance the schedule (conditional on the value read under the lock) and commit.
                    int advanced = await JobCommand.Create(c, t, sql.AdvanceRecurring)
                        .With("next_run", fire.NextRunAtUtc, DbType.DateTimeOffset)
                        .With("last_job_id", lastJobId, DbType.Int64)
                        .With("expected_next_run", due.NextRunAtUtc, DbType.DateTimeOffset)
                        .Id(due.Id)
                        .RunAsync(ct);
                    return advanced == 1
                        ? new FireOutcome(true, added, overlap, dedup, due.Queue)
                        : throw new InvalidOperationException($"Recurring schedule '{due.Code}' changed while it was locked.");
                }, cancellationToken);
            }
            catch (Exception ex) when ((DatabaseFailures.IsDatabaseFailure(ex) || ex is InvalidOperationException or ArgumentException or TimeoutException)
                && !cancellationToken.IsCancellationRequested && lockedId != 0)
            {
                // Rolled back, so nothing was written. A deadlock victim (SQL Server 1205: the enqueue's HOLDLOCK key-range probes
                // of concurrent schedulers) is rerun at once, up to 3 attempts; after that the schedule stays due for the next tick
                // and this tick moves on to the others.
                int attempt = attempts[lockedId] = attempts.GetValueOrDefault(lockedId) + 1;
                if (attempt < 3 && DatabaseFailures.IsDatabaseFailure(ex))
                {
                    continue;
                }

                failed++;
                excluded.Add(lockedId);
                failures.Add((lockedCode, ex));
                continue;
            }

            if (outcome is null)
            {
                break;
            }

            if (!outcome.Fired)
            {
                excluded.Add(lockedId);
                continue;
            }

            schedules++;
            enqueued += outcome.Enqueued;
            skippedOverlap += outcome.SkippedOverlap;
            deduplicated += outcome.Deduplicated;
            if (outcome.Enqueued > 0 && outcome.Queue is not null)
            {
                queues.Add(outcome.Queue);
            }
        }

        return new RecurringTickResult(schedules, enqueued, skippedOverlap, deduplicated, failed, [.. queues]) { Failures = failures };
    }

    private sealed record FireOutcome(bool Fired, int Enqueued, int SkippedOverlap, int Deduplicated, string? Queue = null);

    private static async Task<DueRecurringSchedule?> LockDueAsync(JobSql sql, DbConnection c, DbTransaction t, List<long> excluded, CancellationToken ct)
    {
        string ids = string.Join(",", excluded.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        await using DbCommand command = JobCommand.Create(c, t, sql.LockDueRecurring(ids));
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new DueRecurringSchedule(
                reader.ReadLong(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.ReadStringOrNull(4), reader.GetString(5),
                reader.GetString(6), (MisfirePolicy)Convert.ToInt16(reader.GetValue(7), CultureInfo.InvariantCulture), reader.GetBoolean(8),
                reader.ReadUtc(9), reader.ReadUtc(10))
            : null;
    }

    private static async Task<(long Id, string? IdempotencyKey)> JobRefAsync(JobSql sql, DbConnection c, DbTransaction t, Guid guid, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, sql.JobRefByGuid).With("guid_id", guid, DbType.Guid);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.ReadLong(0), reader.ReadStringOrNull(1))
            : throw new InvalidOperationException($"Job {guid} was reported by enqueue but not found.");
    }

    // ---- read ------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<RecurringScheduleState>> ListAsync(CancellationToken cancellationToken)
        => unitOfWork.UseAsync<IReadOnlyList<RecurringScheduleState>>(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            List<RecurringScheduleState> rows = [];
            await using DbCommand command = JobCommand.Create(session.Connection, null, sql.ListRecurring);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new RecurringScheduleState(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(5), reader.GetString(6),
                    (MisfirePolicy)Convert.ToInt16(reader.GetValue(7), CultureInfo.InvariantCulture), reader.GetBoolean(8), reader.ReadInt(9),
                    reader.GetBoolean(10), reader.ReadGuidOrNull(11), reader.ReadUtc(12), reader.ReadUtcOrNull(13),
                    reader.IsDBNull(14) ? null : reader.ReadLong(14)));
            }

            return rows;
        }, cancellationToken);
}