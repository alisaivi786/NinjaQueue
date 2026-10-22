namespace NinjaQueue.EntityFrameworkCore.Sql;

// Continuations (§15.6, Release 2): the parent row lock that closes the create-vs-complete race, and the set-based release
// or cancel of Awaiting children inside the parent's outcome transaction. Executed on both providers by the integration suite.
internal abstract partial class JobSql
{
    /// <summary>
    /// The statement's own clock. PostgreSQL's now() is the transaction start, so a child created while the parent's outcome
    /// transaction ran would look "in the future" at its release; continuations compare run_at_utc with this clock instead.
    /// </summary>
    public abstract string StatementNow { get; }

    /// <summary>Locks the parent by its public id: (id, status, tenant_scope).</summary>
    public abstract string LockParent { get; }

    /// <summary>The id of the job @guid_id (SQL Server: a forced seek of ux_background_jobs_guid_id, like the other probes).</summary>
    public string JobIdByGuid => IsPostgres
        ? $"SELECT id FROM {S}.background_jobs WHERE guid_id = @guid_id"
        : $"SELECT id FROM {S}.background_jobs WITH (INDEX(ux_background_jobs_guid_id), FORCESEEK) WHERE guid_id = @guid_id";

    /// <summary>Links a just-inserted child to its parent with its fate: @status, @terminal_reason (null unless cancelled).</summary>
    public string LinkContinuation => $"""
        UPDATE {S}.background_jobs
           SET parent_job_id = @parent_id, continuation_policy = @policy, status = @status, terminal_reason_code = @terminal_reason,
               completed_at_utc = CASE WHEN @status >= 10 THEN {Now} ELSE NULL END, updated_at_utc = {Now}
         WHERE id = @id
        """;

    /// <summary>Releases @parent_id's Awaiting children (all when @release_all = 1, else OnParentCompletion only); returns their ids.</summary>
    public abstract string ReleaseChildren { get; }

    /// <summary>Cancels @parent_id's OnParentSuccess Awaiting children with @terminal_reason; returns their ids.</summary>
    public abstract string CancelChildren { get; }

    /// <summary>Queue rows for released children (the id list is formatted by the store from database ids).</summary>
    public string InsertQueueRows(string ids) => $"""
        INSERT INTO {S}.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, concurrency_key, attempts_left)
        SELECT id, queue, job_type, CASE WHEN run_at_utc > {StatementNow} THEN run_at_utc ELSE {StatementNow} END, tenant_scope, concurrency_key, max_attempts - attempt
          FROM {S}.background_jobs WHERE id IN ({ids})
        """;

    public string EventsFor(string ids) => $"""
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        SELECT id, @event_code, {Now}, NULL, @actor_id, @reason, @data FROM {S}.background_jobs WHERE id IN ({ids})
        """;

    public string DeleteActiveKeys(string ids) => $"DELETE FROM {S}.job_active_keys WHERE job_id IN ({ids})";

    public string DeleteUnleasedQueueRows(string ids) => $"DELETE FROM {S}.job_queue WHERE job_id IN ({ids}) AND lease_token IS NULL";
}

internal sealed partial class PostgresJobSql
{
    public override string StatementNow => "clock_timestamp()";

    public override string LockParent => $"SELECT id, status, tenant_scope FROM {S}.background_jobs WHERE guid_id = @guid_id FOR UPDATE";

    public override string ReleaseChildren => $"""
        UPDATE {S}.background_jobs
           SET status = CASE WHEN run_at_utc > clock_timestamp() THEN 2 ELSE 1 END, updated_at_utc = now()
         WHERE parent_job_id = @parent_id AND status = 3 AND (@release_all = 1 OR continuation_policy = 2)
        RETURNING id
        """;

    public override string CancelChildren => $"""
        UPDATE {S}.background_jobs
           SET status = 11, terminal_reason_code = @terminal_reason, completed_at_utc = now(), updated_at_utc = now()
         WHERE parent_job_id = @parent_id AND status = 3 AND continuation_policy = 1
        RETURNING id
        """;
}

internal sealed partial class SqlServerJobSql
{
    public override string StatementNow => "SYSDATETIMEOFFSET()";

    public override string LockParent => $"""
        SELECT id, status, tenant_scope FROM {S}.background_jobs WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK) WHERE guid_id = @guid_id
        """;

    // Runs in every outcome transaction, so it must touch only this parent's Awaiting children: the filtered index
    // ix_background_jobs_parent_awaiting is forced. A scan here would take update locks across background_jobs and
    // deadlock concurrent outcome transactions (observed under load before the hint).
    public override string ReleaseChildren => $"""
        UPDATE b
           SET status = CASE WHEN b.run_at_utc > SYSDATETIMEOFFSET() THEN 2 ELSE 1 END, updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.id
          FROM {S}.background_jobs AS b WITH (INDEX(ix_background_jobs_parent_awaiting), ROWLOCK, READCOMMITTEDLOCK)
         WHERE b.parent_job_id = @parent_id AND b.status = 3 AND (@release_all = 1 OR b.continuation_policy = 2)
        """;

    public override string CancelChildren => $"""
        UPDATE b
           SET status = 11, terminal_reason_code = @terminal_reason, completed_at_utc = SYSDATETIMEOFFSET(), updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.id
          FROM {S}.background_jobs AS b WITH (INDEX(ix_background_jobs_parent_awaiting), ROWLOCK, READCOMMITTEDLOCK)
         WHERE b.parent_job_id = @parent_id AND b.status = 3 AND b.continuation_policy = 1
        """;
}