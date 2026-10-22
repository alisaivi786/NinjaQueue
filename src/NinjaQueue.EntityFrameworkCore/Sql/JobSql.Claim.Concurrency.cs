namespace NinjaQueue.EntityFrameworkCore.Sql;

// The keyed claim (§14.5, Release 2): concurrency keys and MaxRunningPerTenant slots. The candidate scan skips keys that
// already have a leased row; the store takes at most one row per key (and one free slot per slot job) and leases it. The
// backstop is the unique index ux_job_queue_running_concurrency_key: in the rare race where two workers lease the same
// key, one statement fails with a unique violation, which rolls back that claim transaction only. Executed on both
// providers by the integration suite.
internal abstract partial class JobSql
{
    /// <summary>The LIKE pattern of every slot group / slot key (the prefix has no LIKE wildcards).</summary>
    protected static string SlotPattern => Enqueue.ConcurrencyKeys.SlotPrefix + "%";

    /// <summary>Locks up to @scan claimable keyed rows (skipping keys already running): (job_id, tenant_scope, concurrency_key).</summary>
    public abstract string KeyedCandidates { get; }

    /// <summary>The leased slot keys of one (tenant_scope, slot group): @tenant_scope, @pattern.</summary>
    public abstract string LeasedSlots { get; }

    /// <summary>Leases one keyed row under @concurrency_key with @token; returns lease_expires_at_utc (no row = taken meanwhile).</summary>
    public abstract string LeaseKeyed { get; }
}

internal sealed partial class PostgresJobSql
{
    // One LATERAL seek of ix_job_queue_claim_type per job type, merged by the FIFO order (like the unkeyed claim).
    public override string KeyedCandidates => $"""
        SELECT x.job_id, x.tenant_scope, x.concurrency_key
          FROM unnest(CAST(@types AS text[])) AS t(job_type)
          CROSS JOIN LATERAL (
            SELECT q.job_id, q.tenant_scope, q.concurrency_key, q.available_at_utc
              FROM {S}.job_queue q
             WHERE q.queue = @queue
               AND q.job_type = t.job_type
               AND q.lease_token IS NULL
               AND q.available_at_utc <= now()
               AND q.concurrency_key IS NOT NULL
               AND q.attempts_left >= 1
               AND (q.concurrency_key LIKE '{SlotPattern}'
                    OR NOT EXISTS (SELECT 1 FROM {S}.job_queue r
                                    WHERE r.tenant_scope = q.tenant_scope AND r.concurrency_key = q.concurrency_key AND r.lease_token IS NOT NULL))
             ORDER BY q.available_at_utc, q.job_id
             LIMIT @scan
             FOR UPDATE OF q SKIP LOCKED) AS x
         ORDER BY x.available_at_utc, x.job_id
         LIMIT @scan
        """;

    public override string LeasedSlots => $"""
        SELECT concurrency_key FROM {S}.job_queue
         WHERE tenant_scope = @tenant_scope AND lease_token IS NOT NULL AND concurrency_key LIKE @pattern
        """;

    public override string LeaseKeyed => $"""
        UPDATE {S}.job_queue
           SET lease_owner = @worker_id, lease_token = @token, attempts_left = attempts_left - 1,
               lease_expires_at_utc = now() + (@lease_ms * interval '1 millisecond'), concurrency_key = @concurrency_key
         WHERE job_id = @id AND lease_token IS NULL
        RETURNING lease_expires_at_utc
        """;
}

internal sealed partial class SqlServerJobSql
{
    // The probes of running keys read uncommitted (READUNCOMMITTED): a running key's row can be held by an open fenced step
    // for its whole step, and a locking read would wait on it with RCSI OFF. The probe is advisory; the unique index enforces.
    public override string KeyedCandidates => $"""
        SELECT TOP (@scan) x.job_id, x.tenant_scope, x.concurrency_key
          FROM OPENJSON(@types) WITH (job_type nvarchar({JobLimits.MaxJobTypeLength}) '$') AS t
         CROSS APPLY (
           SELECT TOP (@scan) q.job_id, q.tenant_scope, q.concurrency_key, q.available_at_utc
             FROM {S}.job_queue AS q WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK, INDEX(ix_job_queue_claim_type))
            WHERE q.queue = @queue
              AND q.job_type = t.job_type
              AND q.lease_token IS NULL
              AND q.available_at_utc <= SYSDATETIMEOFFSET()
              AND q.concurrency_key IS NOT NULL
              AND q.attempts_left >= 1
              AND (q.concurrency_key LIKE '{SlotPattern}'
                   OR NOT EXISTS (SELECT 1 FROM {S}.job_queue AS r WITH (READUNCOMMITTED)
                                   WHERE r.tenant_scope = q.tenant_scope AND r.concurrency_key = q.concurrency_key AND r.lease_token IS NOT NULL))
            ORDER BY q.available_at_utc, q.job_id) AS x
         ORDER BY x.available_at_utc, x.job_id
        """;

    public override string LeasedSlots => $"""
        SELECT concurrency_key FROM {S}.job_queue WITH (READUNCOMMITTED)
         WHERE tenant_scope = @tenant_scope AND lease_token IS NOT NULL AND concurrency_key LIKE @pattern
        """;

    public override string LeaseKeyed => $"""
        UPDATE {S}.job_queue WITH (ROWLOCK)
           SET lease_owner = @worker_id, lease_token = @token, attempts_left = attempts_left - 1,
               lease_expires_at_utc = DATEADD(millisecond, @lease_ms, SYSDATETIMEOFFSET()), concurrency_key = @concurrency_key
        OUTPUT inserted.lease_expires_at_utc
         WHERE job_id = @id AND lease_token IS NULL
        """;
}