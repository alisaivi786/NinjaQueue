namespace NinjaQueue.Storage;

/// <summary>
/// The storage contract the engine runs on (implemented by NinjaQueue.EntityFrameworkCore for SQL Server and PostgreSQL). Every
/// method that changes a leased job is conditional on its lease token, and every timestamp comes from the database clock.
/// Resolved per DI scope, so a fenced step shares the scope's ambient unit of work with the handler's repositories.
/// </summary>
public interface IJobStore
{
    /// <summary>Writes a job in the caller's open transaction (§15.3). Throws <see cref="NoAmbientTransactionException"/>
    /// when there is none.</summary>
    Task<JobId> StageAsync(NewJob job, CancellationToken cancellationToken);

    /// <summary>Writes jobs in their own transaction(s) and returns their ids in order.</summary>
    Task<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken);

    /// <summary>Claims up to <paramref name="maxJobs"/> ready jobs of <paramref name="jobTypes"/> on <paramref name="queue"/>
    /// (§15.2): new lease token, attempt charged, attempt row written, all in one short transaction.</summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string queue, IReadOnlyList<string> jobTypes, string workerId, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one fenced step (§15.1, §16.4, §17.1): opens the step transaction at READ COMMITTED, fences the lease as its
    /// first statement, runs <paramref name="step"/> inside an ambient-join scope, then finalizes and commits — or rolls
    /// everything back and classifies why.
    /// </summary>
    Task<FencedStepOutcome> RunFencedStepAsync(
        ClaimedJob job, FencedStepSettings settings, Func<string?, CancellationToken, Task<FencedStepResult>> step, CancellationToken cancellationToken);

    /// <summary>Renews a standard job's lease from its own connection and reports a pending cancel (§15.2).</summary>
    Task<FenceResult> RenewAsync(ClaimedJob job, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Stores progress for a standard job (lease-checked) and renews its lease.</summary>
    Task<bool> ReportProgressAsync(ClaimedJob job, int processed, int? total, TimeSpan leaseDuration, CancellationToken cancellationToken);

    /// <summary>Records the outcome of an attempt in one short lease-checked transaction (§14.1, §15.7, §17.1).</summary>
    Task<OutcomeResult> RecordOutcomeAsync(ClaimedJob job, AttemptOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>A validated job ready to be written.</summary>
public sealed record NewJob(
    Guid GuidId,
    string JobType,
    string Queue,
    Guid? TenantId,
    Guid TenantScope,
    string Payload,
    string? IdempotencyKey,
    IdempotencyConflict OnIdempotencyConflict,
    string? UniqueKey,
    UniqueKeyConflict OnUniqueKeyConflict,
    int MaxAttempts,
    DateTimeOffset? RunAtUtc,
    string? CorrelationId,
    string? CausationId,
    string? TraceParent,
    Guid? ActorId,
    string? Source)
{
    /// <summary>The running-concurrency key (§14.5): the caller's key, or the per-tenant slot group of a
    /// <see cref="JobTypeAttribute.MaxRunningPerTenant"/> type (see <c>ConcurrencyKeys</c>). Null when unkeyed.</summary>
    public string? ConcurrencyKey { get; init; }

    /// <summary>The parent of a continuation (§15.6).</summary>
    public JobId? ContinueAfter { get; init; }

    /// <summary>When the continuation runs.</summary>
    public ContinuationPolicy ContinuationPolicy { get; init; } = ContinuationPolicy.OnParentSuccess;
}

/// <summary>A job this worker holds the lease on.</summary>
public sealed record ClaimedJob(
    long Id,
    JobId JobId,
    string JobType,
    Guid? TenantId,
    Guid LeaseToken,
    string WorkerId,
    DateTimeOffset LeaseExpiresAtUtc,
    int Attempt,
    int AttemptSeq,
    int MaxAttempts,
    string? Payload,
    string? Checkpoint,
    string? CorrelationId,
    string? TraceParent,
    int SnoozeCount,
    int ReleaseCount,
    DateTimeOffset CreatedAtUtc);

/// <summary>The provider-neutral result of a fence or renewal (§16.1).</summary>
public readonly record struct FenceResult(bool Matched, DateTimeOffset? LeaseExpiresAtUtc, DateTimeOffset? CancelRequestedAtUtc)
{
    /// <summary>The lease is gone.</summary>
    public static FenceResult Lost => new(false, null, null);
}

/// <summary>Per-step settings for a fenced step.</summary>
public sealed record FencedStepSettings(TimeSpan LeaseDuration, TimeSpan StepTimeout, bool Interruptible);

/// <summary>What the handler's step produced.</summary>
public sealed record FencedStepResult(bool IsComplete, string? Checkpoint, int? ProgressProcessed, int? ProgressTotal);

/// <summary>How a fenced step ended.</summary>
public enum FencedStepStatus
{
    /// <summary>The step committed and the job continues from the new checkpoint.</summary>
    Continued = 1,

    /// <summary>The step committed together with the job's completion.</summary>
    Completed = 2,

    /// <summary>The lease was lost (at the fence or at finalization); nothing was committed and nothing recorded.</summary>
    LeaseLost = 3,

    /// <summary>A cancel won (§17.1); the step rolled back and the job is Cancelled.</summary>
    Cancelled = 4,

    /// <summary>The job's state changed underneath the step; it rolled back and a conflict was recorded.</summary>
    StateConflict = 5,

    /// <summary>The handler threw; the step rolled back. The caller records the attempt outcome.</summary>
    Failed = 6,

    /// <summary>Ownership could not be confirmed (the database was unreachable); the caller self-fences.</summary>
    OwnershipUnconfirmed = 7,
}

/// <summary>The result of <see cref="IJobStore.RunFencedStepAsync"/>.</summary>
public sealed record FencedStepOutcome(FencedStepStatus Status, string? Checkpoint = null, Exception? Error = null);

/// <summary>The kinds of attempt outcome the engine records after the handler returns or throws.</summary>
public enum AttemptOutcomeKind
{
    /// <summary>Standard job succeeded.</summary>
    Succeeded = 1,

    /// <summary>Retry after <see cref="AttemptOutcome.Delay"/> (database clock), charged (or dead-letter when no attempts remain).</summary>
    Failed = 2,

    /// <summary>Timed out: like <see cref="Failed"/>.</summary>
    TimedOut = 3,

    /// <summary>Permanent failure: dead-letter now.</summary>
    DeadLetter = 4,

    /// <summary>Cancelled; not charged.</summary>
    Cancelled = 5,

    /// <summary>Snoozed; refunded.</summary>
    Snoozed = 6,

    /// <summary>Released on shutdown; refunded.</summary>
    Released = 7,

    /// <summary>Deferred (unknown type at runtime); refunded.</summary>
    Deferred = 8,

    /// <summary>Recovery of an expired lease (crash, hang): charged; rescheduled, dead-lettered or cancelled.</summary>
    LeaseExpired = 9,
}

/// <summary>An attempt outcome to record.</summary>
public sealed record AttemptOutcome(
    AttemptOutcomeKind Kind,
    TimeSpan? Delay = null,
    string? ErrorCode = null,
    string? Error = null,
    string? ExceptionType = null,
    string? EventCode = null,
    JobTerminalReason? TerminalReason = null);

/// <summary>What <see cref="IJobStore.RecordOutcomeAsync"/> did.</summary>
public enum OutcomeResult
{
    /// <summary>Recorded as requested.</summary>
    Recorded = 1,

    /// <summary>The lease was gone: nothing recorded.</summary>
    LeaseLost = 2,

    /// <summary>A retry was requested but no attempt remained (or the job was too old): dead-lettered instead.</summary>
    DeadLettered = 3,

    /// <summary>A retry or refund was requested but a cancel was pending: cancelled instead.</summary>
    Cancelled = 4,
}