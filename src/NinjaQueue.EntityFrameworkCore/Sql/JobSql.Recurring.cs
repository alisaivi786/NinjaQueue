namespace NinjaQueue.EntityFrameworkCore.Sql;

// Recurring schedules (§14.6, §15.8) and the PostgreSQL wake-up. Executed on both providers by the Phase 6b suite.
internal abstract partial class JobSql
{
    private const string RecurringColumns =
        "id, code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, definition_version, enabled, paused_by, next_run_at_utc, last_run_at_utc, last_job_id";

    public string DatabaseNow => $"SELECT {Now}";

    /// <summary>Locks the schedule row of @code (SQL Server: also the key range, so a concurrent insert of the same code waits).</summary>
    public abstract string LockRecurringByCode { get; }

    /// <summary>Inserts a schedule; returns its id, or no row when another instance inserted the code first (PostgreSQL).</summary>
    public abstract string InsertRecurring { get; }

    public string UpdateRecurringDefinition => $"""
        UPDATE {S}.job_recurring
           SET job_type = @job_type, queue = @queue, payload = @payload, cron = @cron, time_zone = @time_zone,
               misfire_policy = @misfire_policy, allow_overlap = @allow_overlap, definition_version = @definition_version,
               enabled = @enabled, next_run_at_utc = @next_run, updated_at_utc = {Now}
         WHERE id = @id
        """;

    /// <summary>Disables enabled schedules whose code is not among <paramref name="codeParameters"/> (never deletes them).</summary>
    public string DisableRemovedRecurring(IEnumerable<string> codeParameters) => $"""
        UPDATE {S}.job_recurring SET enabled = {False}, updated_at_utc = {Now}
         WHERE enabled = {True} AND code NOT IN ({string.Join(", ", codeParameters.Select(p => "@" + p))})
        """;

    /// <summary>
    /// Locks one due, enabled schedule, skipping rows another scheduler holds, and returns it with the database clock
    /// (§15.8 step 1). <paramref name="excludedIds"/> are schedules that already failed in this tick.
    /// </summary>
    public abstract string LockDueRecurring(string excludedIds);

    public string AdvanceRecurring => $"""
        UPDATE {S}.job_recurring
           SET next_run_at_utc = @next_run, last_run_at_utc = {Now}, last_job_id = COALESCE(@last_job_id, last_job_id), updated_at_utc = {Now}
         WHERE id = @id AND next_run_at_utc = @expected_next_run
        """;

    /// <summary>The (id, idempotency_key) of the job @guid_id (the occurrence just inserted or deduplicated).</summary>
    public abstract string JobRefByGuid { get; }

    public string ListRecurring => $"SELECT {RecurringColumns} FROM {S}.job_recurring ORDER BY code";

    protected static string Exclude(string excludedIds) => excludedIds.Length == 0 ? "" : $" AND id NOT IN ({excludedIds})";
}

internal sealed partial class PostgresJobSql
{
    public override string JobRefByGuid => $"SELECT id, idempotency_key FROM {S}.background_jobs WHERE guid_id = @guid_id";

    public override string LockRecurringByCode => $"""
        SELECT id, definition_version, enabled, paused_by, cron, time_zone, next_run_at_utc, job_type, queue, payload, misfire_policy, allow_overlap
          FROM {S}.job_recurring WHERE code = @code FOR UPDATE
        """;

    public override string InsertRecurring => $"""
        INSERT INTO {S}.job_recurring
            (code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, definition_version, enabled, paused_by,
             next_run_at_utc, last_run_at_utc, last_job_id, created_at_utc, updated_at_utc)
        VALUES (@code, @job_type, @queue, @payload, @cron, @time_zone, @misfire_policy, @allow_overlap, @definition_version, TRUE, NULL,
                @next_run, NULL, NULL, now(), now())
        ON CONFLICT (code) DO NOTHING
        RETURNING id
        """;

    public override string LockDueRecurring(string excludedIds) => $"""
        SELECT id, code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, next_run_at_utc, now()
          FROM {S}.job_recurring
         WHERE enabled = TRUE AND next_run_at_utc <= now(){Exclude(excludedIds)}
         ORDER BY next_run_at_utc
         LIMIT 1
           FOR UPDATE SKIP LOCKED
        """;
}

internal sealed partial class SqlServerJobSql
{
    // Forced to seek its unique index, like the enqueue key probes: with RCSI OFF a scan of pk_background_jobs would wait on
    // other transactions' uncommitted inserts.
    public override string JobRefByGuid => $"""
        SELECT id, idempotency_key FROM {S}.background_jobs WITH (INDEX(ux_background_jobs_guid_id), FORCESEEK) WHERE guid_id = @guid_id
        """;

    public override string LockRecurringByCode => $"""
        SELECT id, definition_version, enabled, paused_by, cron, time_zone, next_run_at_utc, job_type, queue, payload, misfire_policy, allow_overlap
          FROM {S}.job_recurring WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE code = @code
        """;

    public override string InsertRecurring => $"""
        INSERT INTO {S}.job_recurring
            (code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, definition_version, enabled, paused_by,
             next_run_at_utc, last_run_at_utc, last_job_id, created_at_utc, updated_at_utc)
        OUTPUT inserted.id
        VALUES (@code, @job_type, @queue, @payload, @cron, @time_zone, @misfire_policy, @allow_overlap, @definition_version, 1, NULL,
                @next_run, NULL, NULL, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
        """;

    // READCOMMITTEDLOCK keeps the read a locking read under RCSI, so READPAST skips (not reads past) a row another scheduler holds.
    public override string LockDueRecurring(string excludedIds) => $"""
        SELECT TOP (1) id, code, job_type, queue, payload, cron, time_zone, misfire_policy, allow_overlap, next_run_at_utc, SYSDATETIMEOFFSET()
          FROM {S}.job_recurring WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
         WHERE enabled = 1 AND next_run_at_utc <= SYSDATETIMEOFFSET(){Exclude(excludedIds)}
         ORDER BY next_run_at_utc
        """;
}