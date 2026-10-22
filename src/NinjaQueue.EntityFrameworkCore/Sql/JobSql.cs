namespace NinjaQueue.EntityFrameworkCore.Sql;

/// <summary>
/// Every SQL text the store runs, per provider (§15.2, §16.1, §17.1). Statements that only need a row count are written
/// once with the dialect's small differences (clock, intervals, booleans); statements that return rows are spelled out per
/// provider (RETURNING versus OUTPUT). Every text here is executed by the integration suite on PostgreSQL 16 and on
/// SQL Server 2022 with RCSI ON and OFF (G1): a SQL string is not done until it has run against a real server.
/// </summary>
internal abstract partial class JobSql(string schema)
{
    protected string S { get; } = JobSchemaName.Validate(schema);

    /// <summary>The validated schema name.</summary>
    public string Schema => S;

    public abstract bool IsPostgres { get; }

    /// <summary>The database clock.</summary>
    public abstract string Now { get; }

    /// <summary>The database clock plus <c>@{parameter}</c> milliseconds.</summary>
    public abstract string NowPlusMs(string parameter);

    /// <summary>Milliseconds from <paramref name="column"/> to now.</summary>
    public abstract string MsSince(string column);

    public abstract string True { get; }

    public abstract string False { get; }

    /// <summary>The checkpoint column (a reserved word on SQL Server).</summary>
    public string Checkpoint => IsPostgres ? "checkpoint" : "[checkpoint]";

    // ---- enqueue (§15.3) -------------------------------------------------------------------------------------------

    /// <summary>Inserts a job; returns (id, run_at_utc), or no row on an idempotency conflict (PostgreSQL).</summary>
    public abstract string InsertJob { get; }

    /// <summary>SQL Server: the per-key locked probe that serializes enqueues of one idempotency key. PostgreSQL: null.</summary>
    public abstract string? IdempotencyProbe { get; }

    public string FindIdempotent => $"""
        SELECT id, guid_id FROM {S}.background_jobs
         WHERE tenant_scope = @tenant_scope AND job_type = @job_type AND idempotency_key = @idempotency_key
        """;

    /// <summary>SQL Server: the per-key locked probe for a unique key (returns the holder's guid). PostgreSQL: null.</summary>
    public abstract string? UniqueProbe { get; }

    /// <summary>Reserves a unique key for @id; returns the job id, or no row when the key is held (PostgreSQL).</summary>
    public abstract string ReserveUnique { get; }

    public string UniqueHolder => $"""
        SELECT b.guid_id FROM {S}.job_active_keys k JOIN {S}.background_jobs b ON b.id = k.job_id
         WHERE k.tenant_scope = @tenant_scope AND k.unique_key = @unique_key
        """;

    public string DeleteJob => $"DELETE FROM {S}.background_jobs WHERE id = @id";

    /// <summary>The queue row and the <c>JobEnqueued</c> event of a job just inserted by <see cref="InsertJob"/>: one round trip.</summary>
    public string InsertQueueAndEvent => $"""
        INSERT INTO {S}.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, concurrency_key, attempts_left)
        VALUES (@id, @queue, @job_type, @available_at, @tenant_scope, @concurrency_key, @attempts_left);
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        VALUES (@id, @event_code, {Now}, NULL, @actor_id, NULL, NULL);
        """;

    /// <summary>
    /// Set-based enqueue of jobs with no idempotency key, unique key or parent (nothing to deduplicate or reserve): inserts
    /// the jobs from the JSON array @rows in its "ord" order (so ids keep the caller's order), their queue rows and their
    /// <c>JobEnqueued</c> events (@event_code), in one statement or batch.
    /// </summary>
    public abstract string InsertJobs { get; }

    /// <summary>Rows per <see cref="InsertJobs"/> statement. SQL Server stays near 1,000 key locks per statement on background_jobs.</summary>
    public abstract int MaxRowsPerInsert { get; }

    /// <summary>The columns of one <see cref="InsertJobs"/> row (names and types per provider).</summary>
    protected const string InsertJobsColumns =
        "guid_id, job_type, queue, tenant_id, tenant_scope, status, payload, idempotency_key, unique_key, concurrency_key, max_attempts, attempt, " +
        "snooze_count, release_count, run_at_utc, continuation_policy, correlation_id, causation_id, trace_parent, actor_id, source, created_at_utc, updated_at_utc";

    public string InsertEvent => $"""
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        VALUES (@id, @event_code, {Now}, @worker_id, @actor_id, @reason, @data)
        """;

    // ---- claim (§15.2) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The batch claim of unkeyed jobs, set-based in one round trip (§15.2): leases up to @n claimable rows of @types in
    /// @queue (FIFO by available_at_utc, job_id across the types), marks the jobs Processing and charges the attempt, inserts
    /// their attempt rows and <c>JobClaimed</c> events (@event_code). Each type is a seek of <c>ix_job_queue_claim_type</c>,
    /// so pending rows of other types are never walked. Returns the claimed rows ordered by job id: job_id, lease_token, lease_expires_at_utc, guid_id, job_type, tenant_id, attempt,
    /// max_attempts, payload, checkpoint, correlation_id, trace_parent, snooze_count, release_count, created_at_utc, attempt_seq.
    /// </summary>
    public abstract string Claim { get; }

    /// <summary>
    /// The same marking for jobs this transaction has just leased by key (the keyed claim): the queue rows of the literal
    /// id list <paramref name="ids"/> (formatted by the store from database ids) give the lease. Returns the same columns as <see cref="Claim"/>.
    /// </summary>
    public abstract string MarkLeased(string ids);

    // ---- fence, renew and lease checks (§15.1, §16.1) --------------------------------------------------------------

    /// <summary>
    /// The fence (first statement of a fenced step) and the standard renewal: extends the lease only if @token still holds
    /// it, then reads the cancel flag. PostgreSQL returns one row (lease_expires_at_utc, cancel_requested_at_utc); SQL Server
    /// returns the lease row and, only when it matched, a second result set with the cancel flag.
    /// </summary>
    public abstract string Fence { get; }

    /// <summary>The lease check that opens every outcome transaction: locks the queue row if @token still holds it.</summary>
    public string LeaseCheck => $"UPDATE {S}.job_queue SET lease_expires_at_utc = lease_expires_at_utc WHERE job_id = @id AND lease_token = @token";

    /// <summary>Reads the job under a row lock (status, attempt, max_attempts, cancel, created, snoozes, releases, cancel actor, cancel reason, age in ms by the database clock).</summary>
    public abstract string LockJob { get; }

    /// <summary>
    /// <see cref="LeaseCheck"/> then <see cref="LockJob"/> in one round trip, in the same lock order (queue row, then job row):
    /// returns the <see cref="LockJob"/> row only when @token still holds the lease; no row = lease lost.
    /// </summary>
    public abstract string LockLeasedJob { get; }

    /// <summary>Session-level bound on the open fenced transaction (PostgreSQL: server-side; SQL Server: lock timeout).</summary>
    public abstract string? StepBound(int stepTimeoutMs);

    // ---- fenced finalization (§17.1) -------------------------------------------------------------------------------

    public string FinalizeContinue => $"""
        UPDATE {S}.background_jobs
           SET {Checkpoint} = @checkpoint, progress_processed = COALESCE(@progress_processed, progress_processed),
               progress_total = COALESCE(@progress_total, progress_total), updated_at_utc = {Now}
         WHERE id = @id AND status = 4 AND (cancel_requested_at_utc IS NULL OR @interruptible = {False})
        """;

    public string FinalizeComplete => $"""
        UPDATE {S}.background_jobs
           SET status = 10, terminal_reason_code = 1, completed_at_utc = {Now}, {Checkpoint} = @checkpoint,
               progress_processed = COALESCE(@progress_processed, progress_processed),
               progress_total = COALESCE(@progress_total, progress_total), updated_at_utc = {Now}
         WHERE id = @id AND status = 4 AND (cancel_requested_at_utc IS NULL OR @interruptible = {False})
        """;

    /// <summary>
    /// Non-interruptible completion: records <c>JobCancelTooLate</c> for a cancel requested during the attempt, which applies
    /// only after it (§14.4, §17.1). Runs after <see cref="FinalizeComplete"/>, whose row lock makes the read exact.
    /// </summary>
    public string CancelTooLateIfRequested => $"""
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        SELECT id, @event_code, {Now}, @worker_id, cancel_requested_by, cancel_reason, NULL
          FROM {S}.background_jobs WHERE id = @id AND cancel_requested_at_utc IS NOT NULL
        """;

    // ---- outcome writes (§14.1) ------------------------------------------------------------------------------------

    public string DeleteQueueRow => $"DELETE FROM {S}.job_queue WHERE job_id = @id AND lease_token = @token";

    public string DeleteActiveKey => $"DELETE FROM {S}.job_active_keys WHERE job_id = @id";

    public string CloseAttempt => $"""
        UPDATE {S}.job_attempts
           SET {CloseAttemptSet}
         WHERE job_id = @id AND attempt_seq = @attempt_seq
        """;

    /// <summary>
    /// Ends a leased job in one round trip (Succeeded / Cancelled / DeadLettered): deletes its queue row (if @token still
    /// holds it), then ends the job (@status, @terminal_reason; @refund returns the charged attempt), closes the attempt
    /// (<see cref="CloseAttempt"/>), deletes the unique-key reservation (<see cref="DeleteActiveKey"/>) and writes the event
    /// (@event_code, @worker_id, @actor_id, @reason). Returns 1 when the queue row was deleted; anything else means the
    /// caller must roll back. The caller already holds the queue and job row locks.
    /// </summary>
    public abstract string EndLeased { get; }

    /// <summary>
    /// Puts a leased job back in the queue in one round trip (retry, snooze, release, defer; @refund returns the attempt):
    /// the job (@status, @snoozed, @released), the queue row (available after @delay_ms), <see cref="CloseAttempt"/> and the
    /// event (@event_code, @worker_id, @actor_id, @reason).
    /// </summary>
    public abstract string RescheduleLeased { get; }

    /// <summary>The SET list that ends a job (Succeeded / Cancelled / DeadLettered). @refund returns the charged attempt.</summary>
    protected string EndJobSet => $"""
        status = @status, terminal_reason_code = @terminal_reason, completed_at_utc = {Now},
               attempt = CASE WHEN @refund = {True} THEN attempt - 1 ELSE attempt END,
               last_error_code = COALESCE(@error_code, last_error_code), last_error = COALESCE(@error, last_error),
               updated_at_utc = {Now}
        """;

    /// <summary>The SET list of <see cref="CloseAttempt"/>.</summary>
    protected string CloseAttemptSet => $"""
        finished_at_utc = {Now}, duration_ms = {MsSince("started_at_utc")}, outcome = @outcome,
               charged = CASE WHEN @refund = {True} THEN {False} ELSE charged END,
               error_code = @error_code, error = @error, exception_type = @exception_type
        """;

    /// <summary>The SET list that puts a job back to @status (retry, snooze, release, defer). @refund returns the attempt.</summary>
    protected string RescheduleJobSet => $"""
        status = @status, attempt = CASE WHEN @refund = {True} THEN attempt - 1 ELSE attempt END,
               snooze_count = snooze_count + @snoozed, release_count = release_count + @released,
               last_error_code = COALESCE(@error_code, last_error_code), last_error = COALESCE(@error, last_error),
               updated_at_utc = {Now}
        """;

    // The queue row's SET list. A RetryAfter delay is not capped and can exceed an int of milliseconds (~24.8 days): SQL Server's DATEADD would overflow.
    protected string RescheduleQueueSet => $"""
        lease_owner = NULL, lease_token = NULL, lease_expires_at_utc = NULL, available_at_utc = {NowPlusLongMs("delay_ms")},
               attempts_left = CASE WHEN @refund = {True} THEN attempts_left + 1 ELSE attempts_left END
        """;




    public string UpdateProgress => $"""
        UPDATE {S}.background_jobs SET progress_processed = @progress_processed, progress_total = COALESCE(@progress_total, progress_total),
               updated_at_utc = {Now}
         WHERE id = @id AND status = 4
        """;
}

internal sealed partial class PostgresJobSql(string schema) : JobSql(schema)
{
    public override bool IsPostgres => true;

    public override string Now => "now()";

    public override string True => "TRUE";

    public override string False => "FALSE";

    public override string NowPlusMs(string parameter) => $"(now() + (@{parameter} * interval '1 millisecond'))";

    public override string MsSince(string column) => $"(EXTRACT(EPOCH FROM (now() - {column})) * 1000)::bigint";

    public override string InsertJob => $"""
        INSERT INTO {S}.background_jobs
            (guid_id, job_type, queue, tenant_id, tenant_scope, status, payload, idempotency_key, unique_key, concurrency_key, max_attempts, attempt,
             snooze_count, release_count, run_at_utc, continuation_policy, correlation_id, causation_id, trace_parent, actor_id,
             source, created_at_utc, updated_at_utc)
        VALUES (@guid_id, @job_type, @queue, @tenant_id, @tenant_scope,
                CASE WHEN @run_at IS NULL OR @run_at <= now() THEN 1 ELSE 2 END,
                @payload, @idempotency_key, @unique_key, @concurrency_key, @max_attempts, 0, 0, 0, COALESCE(@run_at, now()), 1,
                @correlation_id, @causation_id, @trace_parent, @actor_id, @source, now(), now())
        ON CONFLICT (tenant_scope, job_type, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING
        RETURNING id, run_at_utc
        """;

    public override int MaxRowsPerInsert => 500;

    public override string InsertJobs => $"""
        WITH ins AS (
          INSERT INTO {S}.background_jobs ({InsertJobsColumns})
          SELECT r.guid_id, r.job_type, r.queue, r.tenant_id, r.tenant_scope, CASE WHEN r.run_at IS NULL OR r.run_at <= now() THEN 1 ELSE 2 END,
                 r.payload, NULL, NULL, r.concurrency_key, r.max_attempts, 0, 0, 0, COALESCE(r.run_at, now()), 1,
                 r.correlation_id, r.causation_id, r.trace_parent, r.actor_id, r.source, now(), now()
            FROM json_to_recordset(CAST(@rows AS json)) AS r(ord int, guid_id uuid, job_type text, queue text, tenant_id uuid, tenant_scope uuid,
                 payload text, concurrency_key text, max_attempts int, run_at timestamptz, correlation_id text, causation_id text,
                 trace_parent text, actor_id uuid, source text)
           ORDER BY r.ord
          RETURNING id, queue, job_type, run_at_utc, tenant_scope, concurrency_key, max_attempts, actor_id),
        q AS (
          INSERT INTO {S}.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, concurrency_key, attempts_left)
          SELECT id, queue, job_type, run_at_utc, tenant_scope, concurrency_key, max_attempts FROM ins)
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        SELECT id, @event_code, now(), NULL, actor_id, NULL, NULL FROM ins
        """;

    public override string? IdempotencyProbe => null;

    public override string? UniqueProbe => null;

    public override string ReserveUnique => $"""
        INSERT INTO {S}.job_active_keys (tenant_scope, unique_key, job_id, reserved_at_utc)
        VALUES (@tenant_scope, @unique_key, @id, now())
        ON CONFLICT (tenant_scope, unique_key) DO NOTHING
        RETURNING job_id
        """;

    // One LATERAL seek per job type (each LIMIT @n, locked with SKIP LOCKED as it is read), merged by the FIFO order. A
    // worker with several types that all have work can lock up to @n rows per type for the few milliseconds of the claim;
    // the ones not taken are released at its COMMIT. All data-modifying CTEs share one snapshot and touch distinct rows.
    public override string Claim => $"""
        WITH c AS (
          SELECT x.job_id
            FROM unnest(CAST(@types AS text[])) AS t(job_type)
            CROSS JOIN LATERAL (
              SELECT q.job_id, q.available_at_utc
                FROM {S}.job_queue q
               WHERE q.queue = @queue
                 AND q.job_type = t.job_type
                 AND q.lease_token IS NULL
                 AND q.available_at_utc <= now()
                 AND q.concurrency_key IS NULL
                 AND q.attempts_left >= 1
               ORDER BY q.available_at_utc, q.job_id
               LIMIT @n
               FOR UPDATE SKIP LOCKED) AS x
           ORDER BY x.available_at_utc, x.job_id
           LIMIT @n),
        l AS (
          UPDATE {S}.job_queue q
             SET lease_owner = @worker_id, lease_token = gen_random_uuid(), attempts_left = q.attempts_left - 1,
                 lease_expires_at_utc = now() + (@lease_ms * interval '1 millisecond')
            FROM c
           WHERE q.job_id = c.job_id
          RETURNING q.job_id, q.lease_token, q.lease_expires_at_utc){MarkFromLeased}
        """;

    public override string MarkLeased(string ids) => $"""
        WITH l AS (
          SELECT q.job_id, q.lease_token, q.lease_expires_at_utc FROM {S}.job_queue q WHERE q.job_id IN ({ids}) AND q.lease_owner = @worker_id){MarkFromLeased}
        """;

    // The CTEs that follow a leased set "l": mark Processing and charge, the attempt row (its seq is the job's next), the event.
    private string MarkFromLeased => $"""
        ,
        b AS (
          UPDATE {S}.background_jobs b
             SET status = 4, attempt = b.attempt + 1, started_at_utc = COALESCE(b.started_at_utc, now()), updated_at_utc = now()
            FROM l
           WHERE b.id = l.job_id
          RETURNING b.id, b.guid_id, b.job_type, b.tenant_id, b.attempt, b.max_attempts, b.payload, b.checkpoint, b.correlation_id,
                    b.trace_parent, b.snooze_count, b.release_count, b.created_at_utc),
        a AS (
          INSERT INTO {S}.job_attempts (job_id, attempt_seq, charged, worker_id, lease_token, started_at_utc)
          SELECT l.job_id, COALESCE((SELECT MAX(p.attempt_seq) FROM {S}.job_attempts p WHERE p.job_id = l.job_id), 0) + 1, TRUE,
                 @worker_id, l.lease_token, now()
            FROM l
          RETURNING job_id, attempt_seq),
        e AS (
          INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
          SELECT l.job_id, @event_code, now(), @worker_id, NULL, NULL, NULL FROM l)
        SELECT b.id, l.lease_token, l.lease_expires_at_utc, b.guid_id, b.job_type, b.tenant_id, b.attempt, b.max_attempts, b.payload,
               b.checkpoint, b.correlation_id, b.trace_parent, b.snooze_count, b.release_count, b.created_at_utc, a.attempt_seq
          FROM b JOIN l ON l.job_id = b.id JOIN a ON a.job_id = b.id
         ORDER BY b.id
        """;

    public override string Fence => $"""
        UPDATE {S}.job_queue SET lease_expires_at_utc = now() + (@lease_ms * interval '1 millisecond')
         WHERE job_id = @id AND lease_token = @token
        RETURNING lease_expires_at_utc, (SELECT b.cancel_requested_at_utc FROM {S}.background_jobs b WHERE b.id = @id)
        """;

    public override string LockJob => $"""
        SELECT status, attempt, max_attempts, cancel_requested_at_utc, created_at_utc, snooze_count, release_count,
               cancel_requested_by, cancel_reason, {MsSince("created_at_utc")}
          FROM {S}.background_jobs WHERE id = @id FOR UPDATE
        """;

    public override string LockLeasedJob => $"""
        WITH lc AS (
          UPDATE {S}.job_queue SET lease_expires_at_utc = lease_expires_at_utc WHERE job_id = @id AND lease_token = @token RETURNING job_id)
        SELECT b.status, b.attempt, b.max_attempts, b.cancel_requested_at_utc, b.created_at_utc, b.snooze_count, b.release_count,
               b.cancel_requested_by, b.cancel_reason, {MsSince("b.created_at_utc")}
          FROM {S}.background_jobs b JOIN lc ON lc.job_id = b.id
           FOR UPDATE OF b
        """;

    public override string EndLeased => $"""
        WITH d AS (
          DELETE FROM {S}.job_queue WHERE job_id = @id AND lease_token = @token RETURNING job_id),
        j AS (
          UPDATE {S}.background_jobs SET {EndJobSet} WHERE id = (SELECT job_id FROM d)),
        a AS (
          UPDATE {S}.job_attempts SET {CloseAttemptSet} WHERE job_id = (SELECT job_id FROM d) AND attempt_seq = @attempt_seq),
        k AS (
          DELETE FROM {S}.job_active_keys WHERE job_id = (SELECT job_id FROM d)),
        e AS (
          INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
          SELECT job_id, @event_code, now(), @worker_id, @actor_id, @reason, NULL FROM d)
        SELECT count(*) FROM d
        """;

    public override string RescheduleLeased => $"""
        WITH r AS (
          UPDATE {S}.job_queue SET {RescheduleQueueSet} WHERE job_id = @id AND lease_token = @token RETURNING job_id),
        j AS (
          UPDATE {S}.background_jobs SET {RescheduleJobSet} WHERE id = @id),
        a AS (
          UPDATE {S}.job_attempts SET {CloseAttemptSet} WHERE job_id = @id AND attempt_seq = @attempt_seq)
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        VALUES (@id, @event_code, now(), @worker_id, @actor_id, @reason, NULL)
        """;

    public override string? StepBound(int stepTimeoutMs)
        => $"SET LOCAL statement_timeout = {stepTimeoutMs}; SET LOCAL idle_in_transaction_session_timeout = {stepTimeoutMs};";
}

internal sealed partial class SqlServerJobSql(string schema) : JobSql(schema)
{
    public override bool IsPostgres => false;

    public override string Now => "SYSDATETIMEOFFSET()";

    public override string True => "1";

    public override string False => "0";

    public override string NowPlusMs(string parameter) => $"DATEADD(millisecond, @{parameter}, SYSDATETIMEOFFSET())";

    public override string MsSince(string column) => $"DATEDIFF_BIG(millisecond, {column}, SYSDATETIMEOFFSET())";

    public override string InsertJob => $"""
        INSERT INTO {S}.background_jobs
            (guid_id, job_type, queue, tenant_id, tenant_scope, status, payload, idempotency_key, unique_key, concurrency_key, max_attempts, attempt,
             snooze_count, release_count, run_at_utc, continuation_policy, correlation_id, causation_id, trace_parent, actor_id,
             source, created_at_utc, updated_at_utc)
        OUTPUT inserted.id, inserted.run_at_utc
        VALUES (@guid_id, @job_type, @queue, @tenant_id, @tenant_scope,
                CASE WHEN @run_at IS NULL OR @run_at <= SYSDATETIMEOFFSET() THEN 1 ELSE 2 END,
                @payload, @idempotency_key, @unique_key, @concurrency_key, @max_attempts, 0, 0, 0, COALESCE(@run_at, SYSDATETIMEOFFSET()), 1,
                @correlation_id, @causation_id, @trace_parent, @actor_id, @source, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
        """;

    public override int MaxRowsPerInsert => 250;

    // A table variable carries the new ids to the queue and event inserts. INSERT ... SELECT ... ORDER BY assigns the
    // identity values in that order, so ids follow the caller's order. The child inserts force nested loops: their foreign
    // key check against background_jobs is then one seek per row (key locks). A merge/scan check of a few hundred rows takes
    // page-level S locks, which with RCSI OFF deadlocked with concurrent claims updating those pages (1205, Phase 8 perf).
    public override string InsertJobs => $"""
        DECLARE @ins TABLE (id bigint NOT NULL PRIMARY KEY, queue nvarchar({JobLimits.MaxQueueLength}) NOT NULL, job_type nvarchar({JobLimits.MaxJobTypeLength}) NOT NULL,
            run_at_utc datetimeoffset NOT NULL, tenant_scope uniqueidentifier NOT NULL, concurrency_key nvarchar({JobLimits.MaxKeyLength}) NULL,
            max_attempts int NOT NULL, actor_id uniqueidentifier NULL);
        INSERT INTO {S}.background_jobs ({InsertJobsColumns})
        OUTPUT inserted.id, inserted.queue, inserted.job_type, inserted.run_at_utc, inserted.tenant_scope, inserted.concurrency_key,
               inserted.max_attempts, inserted.actor_id INTO @ins
        SELECT r.guid_id, r.job_type, r.queue, r.tenant_id, r.tenant_scope,
               CASE WHEN r.run_at IS NULL OR r.run_at <= SYSDATETIMEOFFSET() THEN 1 ELSE 2 END,
               r.payload, NULL, NULL, r.concurrency_key, r.max_attempts, 0, 0, 0, COALESCE(r.run_at, SYSDATETIMEOFFSET()), 1,
               r.correlation_id, r.causation_id, r.trace_parent, r.actor_id, r.source, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
          FROM OPENJSON(@rows) WITH (ord int, guid_id uniqueidentifier, job_type nvarchar({JobLimits.MaxJobTypeLength}), queue nvarchar({JobLimits.MaxQueueLength}),
               tenant_id uniqueidentifier, tenant_scope uniqueidentifier, payload nvarchar(max), concurrency_key nvarchar({JobLimits.MaxKeyLength}),
               max_attempts int, run_at datetimeoffset, correlation_id nvarchar(100), causation_id nvarchar(100), trace_parent nvarchar(100),
               actor_id uniqueidentifier, source nvarchar(100)) AS r
         ORDER BY r.ord;
        INSERT INTO {S}.job_queue (job_id, queue, job_type, available_at_utc, tenant_scope, concurrency_key, attempts_left)
        SELECT id, queue, job_type, run_at_utc, tenant_scope, concurrency_key, max_attempts FROM @ins ORDER BY id
        OPTION (LOOP JOIN);
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        SELECT id, @event_code, SYSDATETIMEOFFSET(), NULL, actor_id, NULL, NULL FROM @ins ORDER BY id
        OPTION (LOOP JOIN);
        """;

    // The key probes serialize enqueues of the SAME key with an exact, transaction-owned application lock on a hash of the
    // key, then read without hints. They used to read WITH (UPDLOCK, HOLDLOCK): for a key that does not exist yet that takes
    // serializable key-range locks on the gap in the unique index, and two transactions holding RangeS-U on one gap then each
    // waited for the other's RangeI-N insert (deadlock 1205, RCSI ON and OFF). An application lock covers only its own key,
    // is released at COMMIT/ROLLBACK, and lets the plain read that follows see the committed winner (RCSI included). A lock
    // not granted (timeout, or a deadlock between two transactions enqueuing several keys in opposite orders) raises 51205.
    // The reads are forced to SEEK their unique index: on a small table the optimizer otherwise scans pk_background_jobs, and
    // with RCSI OFF that scan waits for S locks on other transactions' uncommitted inserts of other keys, while they wait for
    // its own insert (deadlock 1205, confirmed from the system_health deadlock graph). A seek touches only the probed key.
    public override string? IdempotencyProbe => $"""
        {KeyLock("idem", "CONCAT(@job_type, N'|', @idempotency_key)")}
        SELECT id, guid_id FROM {S}.background_jobs WITH (INDEX(ux_background_jobs_idempotency), FORCESEEK)
         WHERE tenant_scope = @tenant_scope AND job_type = @job_type AND idempotency_key = @idempotency_key AND idempotency_key IS NOT NULL;
        """;

    public override string? UniqueProbe => $"""
        {KeyLock("uniq", "@unique_key")}
        SELECT b.guid_id FROM {S}.job_active_keys AS k WITH (INDEX(pk_job_active_keys), FORCESEEK)
          JOIN {S}.background_jobs AS b WITH (FORCESEEK) ON b.id = k.job_id
         WHERE k.tenant_scope = @tenant_scope AND k.unique_key = @unique_key;
        """;

    // Resource: "ninjaqueue:{schema}:{kind}:{tenant_scope}:{sha256(key) hex}" (under the 255-character limit for any key).
    private string KeyLock(string kind, string keyExpression) => $"""
        DECLARE @ninja_key_lock int, @ninja_key_resource nvarchar(255) = CONCAT(N'ninjaqueue:{S}:{kind}:', CONVERT(nvarchar(36), @tenant_scope), N':',
            CONVERT(nvarchar(64), HASHBYTES('SHA2_256', {keyExpression}), 2));
        EXEC @ninja_key_lock = sp_getapplock @Resource = @ninja_key_resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
        IF @ninja_key_lock < 0 THROW 51205, N'NinjaQueue: the enqueue key lock was not granted (timeout or deadlock); retry the transaction.', 1;
        """;

    public override string ReserveUnique => $"""
        INSERT INTO {S}.job_active_keys (tenant_scope, unique_key, job_id, reserved_at_utc)
        OUTPUT inserted.job_id
        VALUES (@tenant_scope, @unique_key, @id, SYSDATETIMEOFFSET())
        """;

    // Identical text whether READ_COMMITTED_SNAPSHOT is ON or OFF; needs compatibility level >= 130 for OPENJSON (§17.6).
    // One CROSS APPLY seek per job type (TOP (@n) each, UPDLOCK + READPAST as it is read), merged by the FIFO order, then the
    // lease through the primary key. The rows read but not taken stay U-locked until COMMIT, a few milliseconds; job_queue
    // has LOCK_ESCALATION = DISABLE (V3), so that never becomes a table lock. The table variables pass the set between the
    // statements of the batch; every join to a jobs table is a forced seek, so no statement scans (RCSI OFF deadlocks).
    public override string Claim => $"""
        DECLARE @l TABLE (job_id bigint NOT NULL PRIMARY KEY, lease_token uniqueidentifier NOT NULL, lease_expires_at_utc datetimeoffset NOT NULL);
        WITH c AS (
          SELECT TOP (@n) x.job_id
            FROM OPENJSON(@types) WITH (job_type nvarchar({JobLimits.MaxJobTypeLength}) '$') AS t
           CROSS APPLY (
             SELECT TOP (@n) q.job_id, q.available_at_utc
               FROM {S}.job_queue AS q WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK, INDEX(ix_job_queue_claim_type))
              WHERE q.queue = @queue
                AND q.job_type = t.job_type
                AND q.lease_token IS NULL
                AND q.available_at_utc <= SYSDATETIMEOFFSET()
                AND q.concurrency_key IS NULL
                AND q.attempts_left >= 1
              ORDER BY q.available_at_utc, q.job_id) AS x
           ORDER BY x.available_at_utc, x.job_id)
        UPDATE q
           SET lease_owner = @worker_id, lease_token = NEWID(), attempts_left = q.attempts_left - 1,
               lease_expires_at_utc = DATEADD(millisecond, @lease_ms, SYSDATETIMEOFFSET())
        OUTPUT inserted.job_id, inserted.lease_token, inserted.lease_expires_at_utc INTO @l
          FROM {S}.job_queue AS q WITH (ROWLOCK, FORCESEEK)
          JOIN c ON c.job_id = q.job_id;
        {MarkFromLeased}
        """;

    public override string MarkLeased(string ids) => $"""
        DECLARE @l TABLE (job_id bigint NOT NULL PRIMARY KEY, lease_token uniqueidentifier NOT NULL, lease_expires_at_utc datetimeoffset NOT NULL);
        INSERT INTO @l (job_id, lease_token, lease_expires_at_utc)
        SELECT q.job_id, q.lease_token, q.lease_expires_at_utc FROM {S}.job_queue AS q WITH (FORCESEEK)
         WHERE q.job_id IN ({ids}) AND q.lease_owner = @worker_id;
        {MarkFromLeased}
        """;

    // The statements that follow a leased set @l: mark Processing and charge (one set-based UPDATE, row locks in id order),
    // then the attempt row (its seq is the job's next) and the event, one row at a time in a server-side loop, still in this
    // one round trip. A multi-row child insert checks its foreign key against background_jobs with page-level S locks (even
    // with nested loops: SQL Server picks the granularity from the row count), and those pages hold rows other claimers are
    // updating under IX: with 8 concurrent claims of 500 that deadlocked (1205, RCSI ON and OFF, Phase 8 contention suite).
    // A single-row insert checks with one key lock on a row this transaction already holds X on, which never conflicts.
    private string MarkFromLeased => $"""
        DECLARE @b TABLE (id bigint NOT NULL PRIMARY KEY, guid_id uniqueidentifier NOT NULL, job_type nvarchar({JobLimits.MaxJobTypeLength}) NOT NULL,
            tenant_id uniqueidentifier NULL, attempt int NOT NULL, max_attempts int NOT NULL, payload nvarchar(max) NULL, [checkpoint] nvarchar(max) NULL,
            correlation_id nvarchar(100) NULL, trace_parent nvarchar(100) NULL, snooze_count int NOT NULL, release_count int NOT NULL,
            created_at_utc datetimeoffset NOT NULL);
        UPDATE b
           SET status = 4, attempt = b.attempt + 1, started_at_utc = COALESCE(b.started_at_utc, SYSDATETIMEOFFSET()), updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.id, inserted.guid_id, inserted.job_type, inserted.tenant_id, inserted.attempt, inserted.max_attempts, inserted.payload,
               inserted.[checkpoint], inserted.correlation_id, inserted.trace_parent, inserted.snooze_count, inserted.release_count,
               inserted.created_at_utc INTO @b
          FROM {S}.background_jobs AS b WITH (ROWLOCK, FORCESEEK)
          JOIN @l AS l ON l.job_id = b.id;
        DECLARE @s TABLE (job_id bigint NOT NULL PRIMARY KEY, attempt_seq int NOT NULL, lease_token uniqueidentifier NOT NULL);
        INSERT INTO @s (job_id, attempt_seq, lease_token)
        SELECT l.job_id, COALESCE(m.seq, 0) + 1, l.lease_token
          FROM @l AS l
         OUTER APPLY (SELECT MAX(p.attempt_seq) AS seq FROM {S}.job_attempts AS p WITH (FORCESEEK) WHERE p.job_id = l.job_id) AS m;
        DECLARE @mark_id bigint = 0, @mark_seq int, @mark_token uniqueidentifier;
        WHILE 1 = 1
        BEGIN
          SELECT TOP (1) @mark_id = job_id, @mark_seq = attempt_seq, @mark_token = lease_token FROM @s WHERE job_id > @mark_id ORDER BY job_id;
          IF @@ROWCOUNT = 0 BREAK;
          INSERT INTO {S}.job_attempts (job_id, attempt_seq, charged, worker_id, lease_token, started_at_utc)
          VALUES (@mark_id, @mark_seq, 1, @worker_id, @mark_token, SYSDATETIMEOFFSET());
          INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
          VALUES (@mark_id, @event_code, SYSDATETIMEOFFSET(), @worker_id, NULL, NULL, NULL);
        END
        SELECT b.id, l.lease_token, l.lease_expires_at_utc, b.guid_id, b.job_type, b.tenant_id, b.attempt, b.max_attempts, b.payload,
               b.[checkpoint], b.correlation_id, b.trace_parent, b.snooze_count, b.release_count, b.created_at_utc, s.attempt_seq
          FROM @b AS b JOIN @l AS l ON l.job_id = b.id JOIN @s AS s ON s.job_id = b.id
         ORDER BY b.id;
        """;

    // §16.1: OUTPUT cannot contain a subquery, so the cancel flag is a second statement in the same batch, read with
    // READCOMMITTEDLOCK so RCSI cannot hand back a stale row version that misses a committed cancel.
    public override string Fence => $"""
        UPDATE {S}.job_queue WITH (ROWLOCK)
           SET lease_expires_at_utc = DATEADD(millisecond, @lease_ms, SYSDATETIMEOFFSET())
        OUTPUT inserted.lease_expires_at_utc
         WHERE job_id = @id AND lease_token = @token;
        IF @@ROWCOUNT = 1
          SELECT b.cancel_requested_at_utc FROM {S}.background_jobs AS b WITH (READCOMMITTEDLOCK) WHERE b.id = @id;
        """;

    public override string LockJob => $"""
        SELECT status, attempt, max_attempts, cancel_requested_at_utc, created_at_utc, snooze_count, release_count,
               cancel_requested_by, cancel_reason, {MsSince("created_at_utc")}
          FROM {S}.background_jobs WITH (UPDLOCK, ROWLOCK, READCOMMITTEDLOCK) WHERE id = @id
        """;

    public override string LockLeasedJob => $"""
        UPDATE {S}.job_queue WITH (ROWLOCK) SET lease_expires_at_utc = lease_expires_at_utc WHERE job_id = @id AND lease_token = @token;
        IF @@ROWCOUNT = 1
          {LockJob};
        """;

    public override string EndLeased => $"""
        DELETE FROM {S}.job_queue WITH (ROWLOCK) WHERE job_id = @id AND lease_token = @token;
        IF @@ROWCOUNT = 1
        BEGIN
          UPDATE {S}.background_jobs WITH (ROWLOCK) SET {EndJobSet} WHERE id = @id;
          UPDATE {S}.job_attempts WITH (ROWLOCK) SET {CloseAttemptSet} WHERE job_id = @id AND attempt_seq = @attempt_seq;
          DELETE FROM {S}.job_active_keys WHERE job_id = @id;
          INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
          VALUES (@id, @event_code, SYSDATETIMEOFFSET(), @worker_id, @actor_id, @reason, NULL);
          SELECT 1;
        END
        """;

    public override string RescheduleLeased => $"""
        UPDATE {S}.background_jobs WITH (ROWLOCK) SET {RescheduleJobSet} WHERE id = @id;
        UPDATE {S}.job_queue WITH (ROWLOCK) SET {RescheduleQueueSet} WHERE job_id = @id AND lease_token = @token;
        UPDATE {S}.job_attempts WITH (ROWLOCK) SET {CloseAttemptSet} WHERE job_id = @id AND attempt_seq = @attempt_seq;
        INSERT INTO {S}.job_events (job_id, event_code, occurred_at_utc, worker_id, actor_id, reason, data)
        VALUES (@id, @event_code, SYSDATETIMEOFFSET(), @worker_id, @actor_id, @reason, NULL);
        """;

    // No session-level SET here: it would outlive the step on the pooled connection. SQL Server bounds an open fenced
    // transaction with the watchdog (§16.3) and per-command timeouts.
    public override string? StepBound(int stepTimeoutMs) => null;
}