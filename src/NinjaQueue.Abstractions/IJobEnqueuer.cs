namespace NinjaQueue;

/// <summary>Enqueues jobs (§3.2, §7.1, §15.3).</summary>
public interface IJobEnqueuer
{
    /// <summary>
    /// Writes the job inside the caller's open database transaction (the transactional outbox): the job commits or rolls
    /// back with the business change. Throws <see cref="NoAmbientTransactionException"/> when no transaction is open; it
    /// never commits on its own. Returns the existing job's id when an idempotency key (or a <c>ReturnExisting</c> unique
    /// key) matches.
    /// </summary>
    Task<JobId> StageAsync<TPayload>(TPayload payload, JobOptions? options = null, CancellationToken cancellationToken = default)
        where TPayload : notnull;

    /// <summary>Writes and commits the job in its own short transaction and returns its id.</summary>
    Task<JobId> EnqueueAsync<TPayload>(TPayload payload, JobOptions? options = null, CancellationToken cancellationToken = default)
        where TPayload : notnull;

    /// <summary>Fan-out: writes many jobs of one type, committed in chunks, and returns their ids in order.</summary>
    Task<IReadOnlyList<JobId>> EnqueueManyAsync<TPayload>(
        IReadOnlyList<(TPayload Payload, JobOptions? Options)> jobs, CancellationToken cancellationToken = default)
        where TPayload : notnull;
}

/// <summary>Per-job options. Defaults come from the job type's <see cref="JobTypeAttribute"/>.</summary>
public sealed record JobOptions
{
    /// <summary>The tenant of a tenant-owned job, from the trusted request context (never the request body).</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Dedupe key, unique per tenant and job type until the job is purged.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>What to do when the idempotency key already exists.</summary>
    public IdempotencyConflict OnIdempotencyConflict { get; init; } = IdempotencyConflict.ReturnExisting;

    /// <summary>At most one non-terminal job per tenant and key, across job types.</summary>
    public string? UniqueKey { get; init; }

    /// <summary>What to do when another active job holds the unique key.</summary>
    public UniqueKeyConflict OnUniqueKeyConflict { get; init; } = UniqueKeyConflict.ReturnExisting;

    /// <summary>
    /// At most one running job per tenant and key, across job types (§14.5): other jobs with the key wait, they are never
    /// rejected. Not allowed on a job type that declares <see cref="JobTypeAttribute.MaxRunningPerTenant"/>.
    /// </summary>
    public string? ConcurrencyKey { get; init; }

    /// <summary>
    /// Makes this job a continuation of another job of the same tenant (§15.6): it waits as <c>Awaiting</c> and is released
    /// (or cancelled) by the parent's outcome according to <see cref="ContinuationPolicy"/>.
    /// </summary>
    public JobId? ContinueAfter { get; init; }

    /// <summary>When a continuation runs (only with <see cref="ContinueAfter"/>). Default: only after the parent succeeds.</summary>
    public ContinuationPolicy ContinuationPolicy { get; init; } = ContinuationPolicy.OnParentSuccess;

    /// <summary>The earliest start time (UTC). Default: now.</summary>
    public DateTimeOffset? RunAt { get; init; }

    /// <summary>Overrides the job type's queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Overrides the job type's attempt budget (1–100).</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>The correlation id to carry into the job.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The id of what caused this job.</summary>
    public string? CausationId { get; init; }

    /// <summary>Who enqueued the job.</summary>
    public Guid? ActorId { get; init; }

    /// <summary>What enqueued the job (a short label).</summary>
    public string? Source { get; init; }
}

/// <summary>Idempotency conflict behaviour.</summary>
public enum IdempotencyConflict
{
    /// <summary>Return the existing job's id.</summary>
    ReturnExisting = 1,

    /// <summary>Throw <see cref="JobConflictException"/>.</summary>
    Throw = 2,
}

/// <summary>Unique-key conflict behaviour.</summary>
public enum UniqueKeyConflict
{
    /// <summary>Return the active job's id.</summary>
    ReturnExisting = 1,

    /// <summary>Throw <see cref="JobConflictException"/>.</summary>
    Reject = 2,
}

/// <summary>An enqueue conflicted with an existing job (idempotency <c>Throw</c> or unique-key <c>Reject</c>).</summary>
public sealed class JobConflictException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JobConflictException(string message, JobId existingJobId)
        : base(message) => ExistingJobId = existingJobId;

    /// <summary>Creates the exception.</summary>
    public JobConflictException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public JobConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public JobConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The job that holds the key.</summary>
    public JobId ExistingJobId { get; }
}

/// <summary><see cref="IJobEnqueuer.StageAsync{TPayload}"/> was called with no open database transaction.</summary>
public sealed class NoAmbientTransactionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public NoAmbientTransactionException()
        : base("StageAsync requires an open database transaction (a ReadWriteWithTransaction / UseTransactionAsync unit of work). Use EnqueueAsync to commit on its own.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public NoAmbientTransactionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public NoAmbientTransactionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}