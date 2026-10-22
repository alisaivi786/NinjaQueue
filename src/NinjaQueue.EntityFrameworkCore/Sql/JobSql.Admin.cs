namespace NinjaQueue.EntityFrameworkCore.Sql;

// Admin (§14.4, §15.11, §17.8, §17.9, Release 2): cancel, Retry/Requeue, continuation restore, search and bulk operations.
// Executed on both providers by the integration suite.
internal abstract partial class JobSql
{
    /// <summary>The summary columns, read by <c>EfCoreJobStore.Admin</c> by ordinal (alias b; parent alias p).</summary>
    public static string SummaryColumns => """
        b.id, b.guid_id, b.job_type, b.queue, b.tenant_id, b.status, b.terminal_reason_code, b.attempt, b.max_attempts, b.created_at_utc,
        b.run_at_utc, b.started_at_utc, b.completed_at_utc, b.last_error_code, b.last_error, p.guid_id, b.unique_key, b.concurrency_key,
        b.cancel_requested_at_utc, b.progress_processed, b.progress_total, b.correlation_id, b.actor_id, b.source, b.tenant_scope
        """;

    /// <summary>A search of at most @take rows, newest first.</summary>
    public abstract string ListJobs(string where);

    /// <summary>Bulk filter ids after @after_id, oldest first, at most @take.</summary>
    public abstract string FindBulkJobs(string where);

    public string JobDetail => $"""
        SELECT {SummaryColumns}, b.payload FROM {S}.background_jobs b LEFT JOIN {S}.background_jobs p ON p.id = b.parent_job_id
         WHERE b.guid_id = @guid_id
        """;

    public string JobAttempts => $"""
        SELECT attempt_seq, charged, worker_id, started_at_utc, finished_at_utc, duration_ms, outcome, error_code, error
          FROM {S}.job_attempts WHERE job_id = @id ORDER BY attempt_seq
        """;

    /// <summary>The newest @take events, oldest first.</summary>
    public abstract string JobEvents { get; }

    public string JobSideEffects => $"""
        SELECT step_key, status, issue_count, external_ref, started_at_utc, completed_at_utc FROM {S}.job_side_effects WHERE job_id = @id ORDER BY started_at_utc
        """;

    /// <summary>Locks the job for an admin action: (status, attempt, tenant_scope, unique_key, job_type).</summary>
    public abstract string LockJobForAdmin { get; }

    /// <summary>Ends a queued or Awaiting job as Cancelled by an admin (status checked again under the lock).</summary>
    public string AdminCancelJob => $"""
        UPDATE {S}.background_jobs
           SET status = 11, terminal_reason_code = 4, completed_at_utc = {Now}, cancel_requested_at_utc = {Now},
               cancel_requested_by = @actor_id, cancel_reason = @reason, updated_at_utc = {Now}
         WHERE id = @id AND status IN (1, 2, 3)
        """;

    /// <summary>Puts a terminal job back to work (§15.11): new budget, cleared terminal and cancel fields, @run_at.</summary>
    public string RunAgainJob => $"""
        UPDATE {S}.background_jobs
           SET status = CASE WHEN @run_at IS NOT NULL AND @run_at > {Now} THEN 2 ELSE 1 END, max_attempts = @max_attempts,
               run_at_utc = COALESCE(@run_at, {Now}),
               terminal_reason_code = NULL, completed_at_utc = NULL,
               cancel_requested_at_utc = NULL, cancel_requested_by = NULL, cancel_reason = NULL,
               snooze_count = CASE WHEN @reset_counters = 1 THEN 0 ELSE snooze_count END,
               release_count = CASE WHEN @reset_counters = 1 THEN 0 ELSE release_count END,
               updated_at_utc = {Now}
         WHERE id = @id AND status = @expected_status
        """;

    /// <summary>Children cancelled because of @parent_id, locked: (id, tenant_scope, unique_key).</summary>
    public abstract string RestorableChildren { get; }

    public string RestoreChild => $"""
        UPDATE {S}.background_jobs SET status = 3, terminal_reason_code = NULL, completed_at_utc = NULL, updated_at_utc = {Now}
         WHERE id = @id AND status = 11 AND terminal_reason_code IN (5, 6)
        """;

    // ---- recurring pause / resume (§14.6, §16.8) ----------------------------------------------------------------------

    /// <summary>Locks a schedule by code: (id, code, enabled, paused_by, cron, time_zone, last_job_id, database now).</summary>
    public abstract string LockRecurringForAdmin { get; }

    public string PauseRecurring => $"UPDATE {S}.job_recurring SET enabled = {False}, paused_by = @paused_by, updated_at_utc = {Now} WHERE id = @id";

    /// <summary>Records an admin-triggered occurrence on its schedule (the next run is unchanged).</summary>
    public string MarkRecurringTriggered => $"""
        UPDATE {S}.job_recurring SET last_job_id = @last_job_id, last_run_at_utc = {Now}, updated_at_utc = {Now} WHERE id = @id
        """;

    public string ResumeRecurring => $"""
        UPDATE {S}.job_recurring SET enabled = {True}, paused_by = NULL, next_run_at_utc = @next_run, updated_at_utc = {Now} WHERE id = @id
        """;

    // ---- stats and workers (§16.8) -----------------------------------------------------------------------------------

    /// <summary>Per queue: (queue, ready, scheduled, running, ms since the oldest ready job) from the hot queue table.</summary>
    public string QueueStats(string where) => $"""
        SELECT q.queue,
               SUM(CASE WHEN q.lease_token IS NULL AND q.available_at_utc <= {Now} THEN 1 ELSE 0 END),
               SUM(CASE WHEN q.lease_token IS NULL AND q.available_at_utc > {Now} THEN 1 ELSE 0 END),
               SUM(CASE WHEN q.lease_token IS NOT NULL THEN 1 ELSE 0 END),
               {MsSince($"MIN(CASE WHEN q.lease_token IS NULL AND q.available_at_utc <= {Now} THEN q.available_at_utc END)")}
          FROM {S}.job_queue q WHERE {where} GROUP BY q.queue ORDER BY q.queue
        """;

    public string AwaitingCount(string where) => $"SELECT count(*) FROM {S}.background_jobs b WHERE b.status = 3 AND {where}";

    /// <summary>(status, count) of jobs that ended since @since (index ix_background_jobs_status_completed).</summary>
    public string TerminalCounts(string where) => $"""
        SELECT b.status, count(*) FROM {S}.background_jobs b WHERE b.status IN (10, 11, 12) AND b.completed_at_utc >= @since AND {where} GROUP BY b.status
        """;

    public string LiveWorkers => $"""
        SELECT worker_id, host, process_id, app_version, queues, job_types, started_at_utc, last_heartbeat_at_utc FROM {S}.job_workers
         WHERE last_heartbeat_at_utc > {NowPlusLongMs("window_ms")} ORDER BY worker_id
        """;

    // ---- bulk operations (§17.9) -------------------------------------------------------------------------------------

    /// <summary>Serializes bulk-operation starts of one tenant for the rest of the transaction.</summary>
    public abstract string BulkTenantLock { get; }

    public static string BulkOperationColumns => """
        guid_id, tenant_scope, operation, actor_id, reason, filter_json, status, requested_count, applied_count, not_eligible_count,
        conflict_count, not_found_count, failed_count, report_json, created_at_utc, completed_at_utc
        """;

    public string FindBulkByIdempotency => $"""
        SELECT {BulkOperationColumns} FROM {S}.job_admin_operations
         WHERE tenant_scope = @tenant_scope AND actor_id = @actor_id AND idempotency_key = @idempotency_key
        """;

    public string FindBulkById => $"SELECT {BulkOperationColumns} FROM {S}.job_admin_operations WHERE guid_id = @guid_id";

    /// <summary>1 when the tenant has an active operation: a synchronous one still running (younger than @window_ms), or a
    /// bulk job holding its <c>bulk-admin:{tenant}</c> reservation.</summary>
    public string ActiveBulkOperation => $"""
        SELECT CASE WHEN EXISTS (SELECT 1 FROM {S}.job_admin_operations
                                  WHERE tenant_scope = @tenant_scope AND status IN (1, 2) AND created_at_utc > {NowPlusLongMs("window_ms")})
                      OR EXISTS (SELECT 1 FROM {S}.job_active_keys WHERE tenant_scope = @system_scope AND unique_key = @bulk_key)
                    THEN 1 ELSE 0 END
        """;

    public string InsertBulkOperation => $"""
        INSERT INTO {S}.job_admin_operations
            (guid_id, tenant_scope, operation, actor_id, reason, idempotency_key, filter_json, status, requested_count, applied_count,
             not_eligible_count, conflict_count, not_found_count, failed_count, report_json, created_at_utc, completed_at_utc)
        VALUES (@guid_id, @tenant_scope, @operation, @actor_id, @reason, @idempotency_key, @filter_json, @status, @requested, 0, 0, 0, 0, 0,
                NULL, {Now}, NULL)
        """;

    public string SaveBulkReport => $"""
        UPDATE {S}.job_admin_operations
           SET status = @status, requested_count = @requested, applied_count = @applied, not_eligible_count = @not_eligible,
               conflict_count = @conflict, not_found_count = @not_found, failed_count = @failed, report_json = @report_json,
               filter_json = COALESCE(@filter_json, filter_json),
               completed_at_utc = CASE WHEN @status >= 3 THEN {Now} ELSE NULL END
         WHERE guid_id = @guid_id
        """;
}

internal sealed partial class PostgresJobSql
{
    public override string ListJobs(string where) => $"""
        SELECT {SummaryColumns} FROM {S}.background_jobs b LEFT JOIN {S}.background_jobs p ON p.id = b.parent_job_id
         WHERE {where}
         ORDER BY b.id DESC
         LIMIT @take
        """;

    public override string FindBulkJobs(string where) => $"""
        SELECT b.id, b.guid_id FROM {S}.background_jobs b WHERE {where} ORDER BY b.id LIMIT @take
        """;

    public override string JobEvents => $"""
        SELECT event_code, occurred_at_utc, worker_id, actor_id, reason, data FROM
          (SELECT id, event_code, occurred_at_utc, worker_id, actor_id, reason, data FROM {S}.job_events WHERE job_id = @id ORDER BY id DESC LIMIT @take) e
         ORDER BY id
        """;

    public override string LockJobForAdmin => $"""
        SELECT status, attempt, tenant_scope, unique_key, job_type FROM {S}.background_jobs WHERE id = @id FOR UPDATE
        """;

    public override string RestorableChildren => $"""
        SELECT id, tenant_scope, unique_key FROM {S}.background_jobs
         WHERE parent_job_id = @parent_id AND status = 11 AND terminal_reason_code IN (5, 6)
         ORDER BY id
         FOR UPDATE
        """;

    public override string BulkTenantLock => "SELECT pg_advisory_xact_lock(@lock_key)";

    public override string LockRecurringForAdmin => $"""
        SELECT id, code, enabled, paused_by, cron, time_zone, last_job_id, now(), job_type, queue, payload, allow_overlap
          FROM {S}.job_recurring WHERE code = @code FOR UPDATE
        """;
}

internal sealed partial class SqlServerJobSql
{
    public override string ListJobs(string where) => $"""
        SELECT TOP (@take) {SummaryColumns} FROM {S}.background_jobs b LEFT JOIN {S}.background_jobs p ON p.id = b.parent_job_id
         WHERE {where}
         ORDER BY b.id DESC
        """;

    public override string FindBulkJobs(string where) => $"""
        SELECT TOP (@take) b.id, b.guid_id FROM {S}.background_jobs b WHERE {where} ORDER BY b.id
        """;

    public override string JobEvents => $"""
        SELECT event_code, occurred_at_utc, worker_id, actor_id, reason, data FROM
          (SELECT TOP (@take) id, event_code, occurred_at_utc, worker_id, actor_id, reason, data FROM {S}.job_events WHERE job_id = @id ORDER BY id DESC) e
         ORDER BY id
        """;

    public override string LockJobForAdmin => $"""
        SELECT status, attempt, tenant_scope, unique_key, job_type FROM {S}.background_jobs WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK) WHERE id = @id
        """;

    public override string RestorableChildren => $"""
        SELECT id, tenant_scope, unique_key FROM {S}.background_jobs WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK)
         WHERE parent_job_id = @parent_id AND status = 11 AND terminal_reason_code IN (5, 6)
         ORDER BY id
        """;

    public override string LockRecurringForAdmin => $"""
        SELECT id, code, enabled, paused_by, cron, time_zone, last_job_id, SYSDATETIMEOFFSET(), job_type, queue, payload, allow_overlap
          FROM {S}.job_recurring WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK) WHERE code = @code
        """;

    public override string BulkTenantLock => """
        DECLARE @result int;
        EXEC @result = sp_getapplock @Resource = @lock_resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
        IF @result < 0 THROW 50001, 'Could not serialize the bulk operation start.', 1;
        """;
}