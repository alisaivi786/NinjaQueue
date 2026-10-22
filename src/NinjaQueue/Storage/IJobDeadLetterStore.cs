namespace NinjaQueue.Storage;

/// <summary>
/// Dead-letter handler storage (plan §18 F7). A dead-lettered job's handler runs in an engine-owned transaction whose
/// first statement claims the marker (<c>dead_letter_handled_at_utc</c>): the claim, the handler's writes and the
/// <c>JobDeadLetterHandled</c> event commit together or not at all, so the handler's writes commit exactly once however
/// many triggers (the executor, the maintenance sweep, two leaders) race for the same job.
/// </summary>
public interface IJobDeadLetterStore
{
    /// <summary>
    /// Dead-lettered jobs of <paramref name="jobTypes"/> whose marker is not set and whose failed handler runs are below
    /// <paramref name="maxHandlerAttempts"/>, oldest first: (job id, job type).
    /// </summary>
    Task<IReadOnlyList<(long Id, string JobType)>> PendingDeadLettersAsync(
        IReadOnlyList<string> jobTypes, int maxHandlerAttempts, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the marker of job <paramref name="jobId"/> and, when <paramref name="handler"/> is not null, runs it in the same
    /// transaction (ambient join, fenced-transaction guard, step bound). Null settles a job whose type has no handler. A
    /// handler failure rolls everything back and is counted; after <paramref name="maxHandlerAttempts"/> failures the job is
    /// abandoned with a <c>JobDeadLetterHandlerAbandoned</c> event.
    /// </summary>
    Task<DeadLetterRun> RunDeadLetterHandlerAsync(
        long jobId, Func<DeadLetteredJob, CancellationToken, Task>? handler, TimeSpan timeout, int maxHandlerAttempts, CancellationToken cancellationToken);
}

/// <summary>A dead-lettered job, as its handler sees it.</summary>
public sealed record DeadLetteredJob(
    long Id,
    JobId JobId,
    string JobType,
    Guid? TenantId,
    int Attempt,
    int MaxAttempts,
    string? Payload,
    string? CorrelationId,
    string? ErrorCode,
    string? Error);

/// <summary>What <see cref="IJobDeadLetterStore.RunDeadLetterHandlerAsync"/> did.</summary>
public enum DeadLetterRunStatus
{
    /// <summary>The handler ran and committed together with the marker.</summary>
    Handled = 1,

    /// <summary>The type has no handler: the marker was set with nothing else.</summary>
    Settled = 2,

    /// <summary>Nothing to do: not dead-lettered, already handled (or being handled), or abandoned.</summary>
    NotPending = 3,

    /// <summary>The handler failed; everything rolled back and the failure was counted. A later sweep retries it.</summary>
    Failed = 4,

    /// <summary>The handler failed for the last allowed time; the job is no longer retried.</summary>
    Abandoned = 5,
}

/// <summary>The result of one dead-letter handler run.</summary>
public sealed record DeadLetterRun(DeadLetterRunStatus Status, int FailedAttempts = 0, Exception? Error = null);