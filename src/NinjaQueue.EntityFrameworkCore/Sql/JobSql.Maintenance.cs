namespace NinjaQueue.EntityFrameworkCore.Sql;

// Recovery, workers, maintenance leases, orphans, purge, queued cancel and the fenced-step watchdog (§7.4, §14.3, §14.6,
// §14.7, §15.9, §16.3). Executed on both providers by the integration suite.
internal abstract partial class JobSql
{
    // ---- expired-lease recovery (§7.4, §15.2) ----------------------------------------------------------------------

    /// <summary>Locks up to @n expired leases, skipping rows held by an open fenced transaction; returns (job_id, lease_token).</summary>
    public abstract string LockExpiredLeases { get; }

    public string CurrentAttemptSeq => $"SELECT COALESCE(MAX(attempt_seq), 1) FROM {S}.job_attempts WHERE job_id = @id";

    public string JobSummary => $"SELECT guid_id, job_type, tenant_id, attempt, max_attempts, created_at_utc FROM {S}.background_jobs WHERE id = @id";

    /// <summary>
    /// The database clock plus <c>@{parameter}</c> milliseconds for spans that can exceed an int of milliseconds (~24.8 days):
    /// retention ages, orphan and liveness windows. SQL Server's DATEADD takes an int, so a 30-day retention overflowed.
    /// </summary>
    public abstract string NowPlusLongMs(string parameter);

    // ---- worker registry (§5.6) ------------------------------------------------------------------------------------

    public abstract string UpsertWorker { get; }

    public string DeleteWorker => $"DELETE FROM {S}.job_workers WHERE worker_id = @worker_id";

    public string LiveWorkerTypes => $"SELECT job_types FROM {S}.job_workers WHERE last_heartbeat_at_utc > {NowPlusLongMs("window_ms")}";

    public string DeleteStaleWorkers => $"DELETE FROM {S}.job_workers WHERE last_heartbeat_at_utc < {NowPlusLongMs("stale_ms")}";

    // ---- maintenance leases (§15.9) --------------------------------------------------------------------------------

    public abstract string EnsureLease { get; }

    public string AcquireOrRenewLease => $"""
        UPDATE {S}.job_leases SET owner = @owner, token = @token, expires_at_utc = {NowPlusMs("ttl_ms")}
         WHERE name = @name AND (expires_at_utc < {Now} OR token = @token)
        """;

    public string ReleaseLease => $"UPDATE {S}.job_leases SET expires_at_utc = {Now} WHERE name = @name AND token = @token";

    // ---- orphans (§14.3) -------------------------------------------------------------------------------------------

    public string PendingTypesOlderThan => $"""
        SELECT DISTINCT q.job_type FROM {S}.job_queue q JOIN {S}.background_jobs b ON b.id = q.job_id
         WHERE q.lease_token IS NULL AND b.orphaned_at_utc IS NULL AND b.created_at_utc < {NowPlusLongMs("age_ms")}
        """;

    /// <summary>Flags pending jobs of @job_type older than the cutoff; returns the ids it flagged.</summary>
    public abstract string FlagOrphans { get; }

    /// <summary>One <c>JobOrphaned</c> event per job flagged in this round (never again for jobs flagged earlier).</summary>
    public string OrphanFlaggedEvents(string ids) => $"""
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, reason)
        SELECT b.id, @event_code, {Now}, @reason FROM {S}.background_jobs b WHERE b.id IN ({ids})
        """;

    public string OrphanEvents => $"""
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, reason)
        SELECT b.id, @event_code, {Now}, @reason FROM {S}.background_jobs b
         WHERE b.job_type = @job_type AND b.orphaned_at_utc IS NOT NULL AND b.status IN (1, 2)
        """;

    public string OrphanedTypes => $"SELECT DISTINCT job_type FROM {S}.background_jobs WHERE orphaned_at_utc IS NOT NULL AND status IN (1, 2)";

    public string ClearOrphans => $"UPDATE {S}.background_jobs SET orphaned_at_utc = NULL, updated_at_utc = {Now} WHERE job_type = @job_type AND orphaned_at_utc IS NOT NULL";

    // ---- two-tier purge (§14.7) ------------------------------------------------------------------------------------

    /// <summary>Ids of terminal jobs of @status completed before the cutoff whose operational data is still present.</summary>
    public abstract string OperationalPurgeCandidates { get; }

    /// <summary>Ids of terminal jobs whose slim audit row is past retention.</summary>
    public abstract string AuditPurgeCandidates { get; }

    /// <summary>Jobs per purge transaction. SQL Server stays well under the 5,000-lock escalation threshold per statement.</summary>
    public abstract int PurgeStatementBatch { get; }

    public string PurgeAttempts(string ids) => $"DELETE FROM {S}.job_attempts WHERE job_id IN ({ids})";

    public string PurgeEvents(string ids) => $"DELETE FROM {S}.job_events WHERE job_id IN ({ids})";

    public string PurgeSideEffects(string ids) => $"DELETE FROM {S}.job_side_effects WHERE job_id IN ({ids})";

    public string SlimJobs(string ids) => $"UPDATE {S}.background_jobs SET payload = NULL, last_error = NULL, {Checkpoint} = NULL WHERE id IN ({ids})";

    public string PurgeJobs(string ids) => $"DELETE FROM {S}.background_jobs WHERE id IN ({ids})";

    // ---- admin: queued cancel (§14.4 first row) --------------------------------------------------------------------

    public string FindJobForAdmin => $"SELECT id, status, tenant_scope FROM {S}.background_jobs WHERE guid_id = @guid_id";

    public string DeleteUnleasedQueueRow => $"DELETE FROM {S}.job_queue WHERE job_id = @id AND lease_token IS NULL";

    /// <summary>Requests the cooperative cancel of a running job (§17.1): blocks only while a finalization holds the row.</summary>
    public abstract string RequestCancel { get; }

    // ---- fenced-step watchdog (§16.3) ------------------------------------------------------------------------------

    /// <summary>The fenced connection's identity: (session id, session start, transaction id).</summary>
    public abstract string SessionIdentity { get; }

    /// <summary>
    /// Drops a registration left by an earlier lease of the same job (its unregister failed, e.g. the database was down when that
    /// step ended). Safe: this step's fence already holds the queue row lock, so the earlier transaction has ended.
    /// </summary>
    public string DropStaleFencedStep => $"DELETE FROM {S}.job_fenced_steps WHERE job_id = @id AND lease_token <> @token";

    public string RegisterFencedStep => $"""
        INSERT INTO {S}.job_fenced_steps (job_id, lease_token, server_session_id, session_start_utc, transaction_id, deadline_utc, owner_worker)
        VALUES (@id, @token, @session_id, @session_start, @transaction_id, {NowPlusMs("deadline_ms")}, @owner)
        """;

    public string UnregisterFencedStep => $"DELETE FROM {S}.job_fenced_steps WHERE job_id = @id AND lease_token = @token";

    public string OverdueFencedSteps => $"""
        SELECT job_id, lease_token, server_session_id, session_start_utc, transaction_id FROM {S}.job_fenced_steps
         WHERE deadline_utc < {NowPlusMs("grace_ms")}
        """;

    /// <summary>1 when the registered session is still the same one inside the same open transaction (guards against reuse).</summary>
    public abstract string SessionStillOpen { get; }

    /// <summary>
    /// Touches every session view the watchdog's identity check reads. It fails (permission or connectivity) exactly when the
    /// watchdog could not confirm or kill a hung session, which the health check reports as Unhealthy (§16.3).
    /// </summary>
    public abstract string SessionViewProbe { get; }

    /// <summary>Kills the registered session. The session id is an integer formatted by the store, never user input.</summary>
    public abstract string KillSession(int sessionId);

    /// <summary>1 when the job runtime may kill other sessions (SQL Server) / signal backends (PostgreSQL).</summary>
    public abstract string CanKillSessions { get; }

    /// <summary>Startup checks: (compatibility/version number, read-committed-snapshot flag or 0).</summary>
    public abstract string ServerChecks { get; }
}

internal sealed partial class PostgresJobSql
{
    public override string NowPlusLongMs(string parameter) => NowPlusMs(parameter);

    public override string FlagOrphans => $"""
        UPDATE {S}.background_jobs SET orphaned_at_utc = now(), updated_at_utc = now()
         WHERE job_type = @job_type AND orphaned_at_utc IS NULL AND status IN (1, 2) AND created_at_utc < {NowPlusLongMs("age_ms")}
        RETURNING id
        """;

    public override string LockExpiredLeases => $"""
        SELECT q.job_id, q.lease_token FROM {S}.job_queue q
         WHERE q.lease_token IS NOT NULL AND q.lease_expires_at_utc < now()
         ORDER BY q.lease_expires_at_utc
         LIMIT @n
         FOR UPDATE SKIP LOCKED
        """;

    public override string UpsertWorker => $"""
        INSERT INTO {S}.job_workers (worker_id, host, process_id, app_version, queues, job_types, started_at_utc, last_heartbeat_at_utc)
        VALUES (@worker_id, @host, @process_id, @app_version, @queues, @job_types, now(), now())
        ON CONFLICT (worker_id) DO UPDATE SET last_heartbeat_at_utc = now(), queues = excluded.queues, job_types = excluded.job_types
        """;

    public override string EnsureLease => $"""
        INSERT INTO {S}.job_leases (name, expires_at_utc) VALUES (@name, '1970-01-01T00:00:00Z') ON CONFLICT (name) DO NOTHING
        """;

    public override int PurgeStatementBatch => 1000;

    public override string OperationalPurgeCandidates => $"""
        SELECT id FROM {S}.background_jobs
         WHERE status = @status AND completed_at_utc < {NowPlusLongMs("age_ms")} AND payload IS NOT NULL
           AND (retain_until_utc IS NULL OR retain_until_utc < now())
         ORDER BY id LIMIT @n
        """;

    public override string AuditPurgeCandidates => $"""
        SELECT b.id FROM {S}.background_jobs b
         WHERE b.status >= 10 AND b.completed_at_utc < {NowPlusLongMs("age_ms")}
           AND (b.retain_until_utc IS NULL OR b.retain_until_utc < now())
           AND NOT EXISTS (SELECT 1 FROM {S}.background_jobs c WHERE c.parent_job_id = b.id)
         ORDER BY b.id LIMIT @n
        """;

    public override string RequestCancel => $"""
        UPDATE {S}.background_jobs SET cancel_requested_at_utc = now(), cancel_requested_by = @actor_id, cancel_reason = @reason, updated_at_utc = now()
         WHERE id = @id AND status < 10 AND cancel_requested_at_utc IS NULL
        """;

    public override string SessionIdentity => "SELECT pg_backend_pid(), backend_start, CAST(NULL AS bigint) FROM pg_stat_activity WHERE pid = pg_backend_pid()";

    public override string SessionViewProbe => "SELECT count(*) FROM pg_stat_activity WHERE pid = pg_backend_pid()";

    public override string SessionStillOpen => """
        SELECT count(*) FROM pg_stat_activity WHERE pid = @session_id AND backend_start = @session_start AND xact_start IS NOT NULL
        """;

    public override string KillSession(int sessionId) => $"SELECT pg_terminate_backend({sessionId})";

    public override string CanKillSessions => """
        SELECT CASE WHEN pg_has_role(current_user, 'pg_signal_backend', 'member')
                      OR (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN 1 ELSE 0 END
        """;

    public override string ServerChecks => "SELECT current_setting('server_version_num')::int, 0";
}

internal sealed partial class SqlServerJobSql
{
    // DATEADD's number is an int: split the span into whole days plus the remaining milliseconds, both of which fit.
    public override string NowPlusLongMs(string parameter)
        => $"DATEADD(millisecond, @{parameter} % 86400000, DATEADD(day, @{parameter} / 86400000, SYSDATETIMEOFFSET()))";

    public override string FlagOrphans => $"""
        UPDATE {S}.background_jobs SET orphaned_at_utc = SYSDATETIMEOFFSET(), updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.id
         WHERE job_type = @job_type AND orphaned_at_utc IS NULL AND status IN (1, 2) AND created_at_utc < {NowPlusLongMs("age_ms")}
        """;

    public override string LockExpiredLeases => $"""
        SELECT TOP (@n) q.job_id, q.lease_token FROM {S}.job_queue AS q WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
         WHERE q.lease_token IS NOT NULL AND q.lease_expires_at_utc < SYSDATETIMEOFFSET()
         ORDER BY q.lease_expires_at_utc
        """;

    public override string UpsertWorker => $"""
        UPDATE {S}.job_workers SET last_heartbeat_at_utc = SYSDATETIMEOFFSET(), queues = @queues, job_types = @job_types WHERE worker_id = @worker_id;
        IF @@ROWCOUNT = 0
          INSERT INTO {S}.job_workers (worker_id, host, process_id, app_version, queues, job_types, started_at_utc, last_heartbeat_at_utc)
          VALUES (@worker_id, @host, @process_id, @app_version, @queues, @job_types, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
        """;

    public override string EnsureLease => $"""
        IF NOT EXISTS (SELECT 1 FROM {S}.job_leases WITH (UPDLOCK, HOLDLOCK) WHERE name = @name)
          INSERT INTO {S}.job_leases (name, expires_at_utc) VALUES (@name, '1970-01-01T00:00:00+00:00');
        """;

    public override int PurgeStatementBatch => 500;

    public override string OperationalPurgeCandidates => $"""
        SELECT TOP (@n) id FROM {S}.background_jobs
         WHERE status = @status AND completed_at_utc < {NowPlusLongMs("age_ms")} AND payload IS NOT NULL
           AND (retain_until_utc IS NULL OR retain_until_utc < SYSDATETIMEOFFSET())
         ORDER BY id
        """;

    public override string AuditPurgeCandidates => $"""
        SELECT TOP (@n) b.id FROM {S}.background_jobs b
         WHERE b.status >= 10 AND b.completed_at_utc < {NowPlusLongMs("age_ms")}
           AND (b.retain_until_utc IS NULL OR b.retain_until_utc < SYSDATETIMEOFFSET())
           AND NOT EXISTS (SELECT 1 FROM {S}.background_jobs c WHERE c.parent_job_id = b.id)
         ORDER BY b.id
        """;

    public override string RequestCancel => $"""
        UPDATE {S}.background_jobs WITH (ROWLOCK, READCOMMITTEDLOCK)
           SET cancel_requested_at_utc = SYSDATETIMEOFFSET(), cancel_requested_by = @actor_id, cancel_reason = @reason, updated_at_utc = SYSDATETIMEOFFSET()
         WHERE id = @id AND status < 10 AND cancel_requested_at_utc IS NULL
        """;

    public override string SessionIdentity => """
        SELECT @@SPID, s.login_time, CAST(CURRENT_TRANSACTION_ID() AS bigint) FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID
        """;

    public override string SessionViewProbe => """
        SELECT count(*) FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_tran_session_transactions t ON t.session_id = s.session_id
         WHERE s.session_id = @@SPID
        """;

    public override string SessionStillOpen => """
        SELECT count(*) FROM sys.dm_exec_sessions s
          JOIN sys.dm_tran_session_transactions t ON t.session_id = s.session_id
         WHERE s.session_id = @session_id AND s.login_time = @session_start AND t.transaction_id = @transaction_id
        """;

    public override string KillSession(int sessionId) => $"KILL {sessionId}";

    public override string CanKillSessions => "SELECT CAST(COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY CONNECTION'), 0) AS int)";

    public override string ServerChecks => """
        SELECT d.compatibility_level, CAST(d.is_read_committed_snapshot_on AS int) FROM sys.databases d WHERE d.name = DB_NAME()
        """;
}