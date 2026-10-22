namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>
/// Set-based seeding for scale tests (100K–1M rows in seconds, where the enqueue API would take minutes). Rows satisfy every
/// CHECK constraint of <c>NinjaQueueSchema.V1</c>; system jobs (tenant scope zero) with the literal payload <c>{}</c>.
/// </summary>
public static class Seeder
{
    private const int Chunk = 100_000;
    private const string SystemScope = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Inserts <paramref name="count"/> ready (Enqueued) jobs of <paramref name="jobType"/> with queue rows. Their
    /// <c>available_at_utc</c> starts <paramref name="readySecondsAgo"/> seconds in the past and rises 1 ms per row.
    /// </summary>
    public static async Task SeedPendingAsync(PerfDatabase db, string jobType, string queue, int count, int readySecondsAgo = 3600)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (int done = 0; done < count; done += Chunk)
        {
            int n = Math.Min(Chunk, count - done);
            long before = await db.ScalarAsync<long>("SELECT COALESCE(MAX(id), 0) FROM ninjaqueue.background_jobs");
            await db.ExecuteAsync(db.IsPostgres
                ? $"""
                   INSERT INTO ninjaqueue.background_jobs (guid_id, job_type, queue, tenant_scope, status, payload, max_attempts, attempt, snooze_count,
                          release_count, run_at_utc, continuation_policy, created_at_utc, updated_at_utc)
                   SELECT gen_random_uuid(), '{jobType}', '{queue}', '{SystemScope}', 1, '{"{}"}', 3, 0, 0, 0,
                          now() - interval '{readySecondsAgo} seconds' + ((g + {done}) * interval '1 millisecond'), 1, now(), now()
                     FROM generate_series(1, {n}) g
                   """
                : $"""
                   WITH n AS (SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                   INSERT INTO ninjaqueue.background_jobs (guid_id, job_type, queue, tenant_scope, status, payload, max_attempts, attempt, snooze_count,
                          release_count, run_at_utc, continuation_policy, created_at_utc, updated_at_utc)
                   SELECT NEWID(), '{jobType}', '{queue}', '{SystemScope}', 1, '{"{}"}', 3, 0, 0, 0,
                          DATEADD(millisecond, i + {done}, DATEADD(second, -{readySecondsAgo}, SYSDATETIMEOFFSET())), 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
                     FROM n
                   """);
            await db.ExecuteAsync($"""
                INSERT INTO ninjaqueue.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, attempts_left)
                SELECT id, queue, job_type, run_at_utc, tenant_scope, max_attempts FROM ninjaqueue.background_jobs WHERE id > {before}
                """);
        }

        await AnalyzeAsync(db);
    }

    /// <summary>
    /// Inserts <paramref name="count"/> Succeeded jobs completed <paramref name="completedDaysAgo"/> days ago, each with one
    /// charged attempt and three events (JobEnqueued, JobClaimed, JobSucceeded): the shape the purge removes.
    /// </summary>
    public static async Task SeedSucceededAsync(PerfDatabase db, string jobType, int count, int completedDaysAgo)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (int done = 0; done < count; done += Chunk)
        {
            int n = Math.Min(Chunk, count - done);
            long before = await db.ScalarAsync<long>("SELECT COALESCE(MAX(id), 0) FROM ninjaqueue.background_jobs");
            string completed = db.IsPostgres ? $"now() - interval '{completedDaysAgo} days'" : $"DATEADD(day, -{completedDaysAgo}, SYSDATETIMEOFFSET())";
            await db.ExecuteAsync(db.IsPostgres
                ? $"""
                   INSERT INTO ninjaqueue.background_jobs (guid_id, job_type, queue, tenant_scope, status, payload, max_attempts, attempt, snooze_count,
                          release_count, run_at_utc, continuation_policy, terminal_reason_code, created_at_utc, started_at_utc, completed_at_utc, updated_at_utc)
                   SELECT gen_random_uuid(), '{jobType}', 'default', '{SystemScope}', 10, '{"{}"}', 3, 1, 0, 0,
                          {completed}, 1, 1, {completed}, {completed}, {completed}, {completed}
                     FROM generate_series(1, {n}) g
                   """
                : $"""
                   WITH n AS (SELECT TOP ({n}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                   INSERT INTO ninjaqueue.background_jobs (guid_id, job_type, queue, tenant_scope, status, payload, max_attempts, attempt, snooze_count,
                          release_count, run_at_utc, continuation_policy, terminal_reason_code, created_at_utc, started_at_utc, completed_at_utc, updated_at_utc)
                   SELECT NEWID(), '{jobType}', 'default', '{SystemScope}', 10, '{"{}"}', 3, 1, 0, 0,
                          {completed}, 1, 1, {completed}, {completed}, {completed}, {completed}
                     FROM n
                   """);
            string token = db.IsPostgres ? "gen_random_uuid()" : "NEWID()";
            string t = db.IsPostgres ? "TRUE" : "1";
            await db.ExecuteAsync($"""
                INSERT INTO ninjaqueue.job_attempts (job_id, attempt_seq, charged, worker_id, lease_token, started_at_utc, finished_at_utc, duration_ms, outcome)
                SELECT id, 1, {t}, 'seed', {token}, completed_at_utc, completed_at_utc, 1, 1 FROM ninjaqueue.background_jobs WHERE id > {before}
                """);
            foreach (string code in (string[])[JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobSucceeded])
            {
                await db.ExecuteAsync($"""
                    INSERT INTO ninjaqueue.job_events (job_id, event_code, occurred_at_utc) SELECT id, '{code}', completed_at_utc FROM ninjaqueue.background_jobs WHERE id > {before}
                    """);
            }
        }

        await AnalyzeAsync(db);
    }

    /// <summary>Refreshes optimizer statistics after a bulk load, as autovacuum / auto-update statistics would.</summary>
    public static Task AnalyzeAsync(PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ExecuteAsync(db.IsPostgres
            ? "ANALYZE ninjaqueue.background_jobs; ANALYZE ninjaqueue.job_queue; ANALYZE ninjaqueue.job_attempts; ANALYZE ninjaqueue.job_events;"
            : "UPDATE STATISTICS ninjaqueue.background_jobs; UPDATE STATISTICS ninjaqueue.job_queue; UPDATE STATISTICS ninjaqueue.job_attempts; UPDATE STATISTICS ninjaqueue.job_events;");
    }
}