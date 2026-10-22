namespace NinjaQueue.EntityFrameworkCore.Sql;

// Dead-letter handlers (plan §18 F7, NinjaQueueSchema.V2). Executed on both providers by the integration suite.
internal abstract partial class JobSql
{
    /// <summary>
    /// The first statement of a dead-letter handler transaction: sets the marker only on a dead-lettered, unhandled,
    /// not-abandoned job and returns (guid_id, job_type, tenant_id, attempt, max_attempts, payload, correlation_id,
    /// last_error_code, last_error); no row = nothing to do. The row lock it takes is held to COMMIT, so a concurrent claim
    /// waits and then matches 0 rows.
    /// </summary>
    public abstract string ClaimDeadLetter { get; }

    /// <summary>Counts a failed handler run on a still-unhandled job; returns the new count (no row when already handled).</summary>
    public abstract string CountDeadLetterFailure { get; }

    /// <summary>Unhandled dead letters of @types under @max_attempts failed runs, oldest first: (id, job_type).</summary>
    public abstract string PendingDeadLetters { get; }
}

internal sealed partial class PostgresJobSql
{
    public override string ClaimDeadLetter => $"""
        UPDATE {S}.background_jobs SET dead_letter_handled_at_utc = now(), updated_at_utc = now()
         WHERE id = @id AND status = 12 AND dead_letter_handled_at_utc IS NULL AND COALESCE(dead_letter_attempts, 0) < @max_attempts
        RETURNING guid_id, job_type, tenant_id, attempt, max_attempts, payload, correlation_id, last_error_code, last_error
        """;

    public override string CountDeadLetterFailure => $"""
        UPDATE {S}.background_jobs SET dead_letter_attempts = COALESCE(dead_letter_attempts, 0) + 1, updated_at_utc = now()
         WHERE id = @id AND status = 12 AND dead_letter_handled_at_utc IS NULL
        RETURNING dead_letter_attempts
        """;

    public override string PendingDeadLetters => $"""
        SELECT id, job_type FROM {S}.background_jobs
         WHERE status = 12 AND dead_letter_handled_at_utc IS NULL AND COALESCE(dead_letter_attempts, 0) < @max_attempts
           AND job_type = ANY(@types)
         ORDER BY id
         LIMIT @n
        """;
}

internal sealed partial class SqlServerJobSql
{
    // READCOMMITTEDLOCK: under RCSI a plain read could see a stale version and both racers would think they claimed it.
    public override string ClaimDeadLetter => $"""
        UPDATE {S}.background_jobs WITH (ROWLOCK, READCOMMITTEDLOCK)
           SET dead_letter_handled_at_utc = SYSDATETIMEOFFSET(), updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.guid_id, inserted.job_type, inserted.tenant_id, inserted.attempt, inserted.max_attempts, inserted.payload,
               inserted.correlation_id, inserted.last_error_code, inserted.last_error
         WHERE id = @id AND status = 12 AND dead_letter_handled_at_utc IS NULL AND COALESCE(dead_letter_attempts, 0) < @max_attempts
        """;

    public override string CountDeadLetterFailure => $"""
        UPDATE {S}.background_jobs WITH (ROWLOCK, READCOMMITTEDLOCK)
           SET dead_letter_attempts = COALESCE(dead_letter_attempts, 0) + 1, updated_at_utc = SYSDATETIMEOFFSET()
        OUTPUT inserted.dead_letter_attempts
         WHERE id = @id AND status = 12 AND dead_letter_handled_at_utc IS NULL
        """;

    // Same OPENJSON type filter as the claim (compatibility level >= 130, §17.6).
    public override string PendingDeadLetters => $"""
        SELECT TOP (@n) id, job_type FROM {S}.background_jobs
         WHERE status = 12 AND dead_letter_handled_at_utc IS NULL AND COALESCE(dead_letter_attempts, 0) < @max_attempts
           AND job_type IN (SELECT [value] FROM OPENJSON(@types))
         ORDER BY id
        """;
}