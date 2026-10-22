namespace NinjaQueue;

/// <summary>
/// A standard job handler (<see cref="JobSideEffects.Idempotent"/>): the engine holds no transaction while it runs, the
/// handler commits its own work, and delivery is at least once (§15.1 path B).
/// </summary>
/// <typeparam name="TPayload">The payload record. Carry ids, not data.</typeparam>
public interface IJobHandler<in TPayload>
{
    /// <summary>Runs one attempt and reports its outcome.</summary>
    Task<JobResult> HandleAsync(TPayload payload, JobContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A database-fenced job handler (<see cref="JobSideEffects.DatabaseFenced"/>). Each call runs one step inside the
/// engine-owned transaction: repositories resolved in the job's scope share it, and the step's writes commit if and only
/// if the engine's finalization commits (§15.1 path A). Do not commit, open a transaction, or call HTTP/SMTP here.
/// </summary>
/// <typeparam name="TPayload">The payload record. Carry ids, not data.</typeparam>
public interface IFencedJobHandler<in TPayload>
{
    /// <summary>Runs one step from <see cref="FencedJobContext.Checkpoint"/> and says whether the job continues or is done.</summary>
    Task<Step> HandleStepAsync(TPayload payload, FencedJobContext context, CancellationToken cancellationToken);
}

/// <summary>The result of one fenced step.</summary>
public sealed class Step
{
    private Step(bool isComplete, string? checkpoint)
    {
        IsComplete = isComplete;
        Checkpoint = checkpoint;
    }

    /// <summary>True when the job is done.</summary>
    public bool IsComplete { get; }

    /// <summary>The checkpoint the next step starts from (null when complete).</summary>
    public string? Checkpoint { get; }

    /// <summary>Commits this step's writes together with <paramref name="checkpoint"/>; the next step starts from it.</summary>
    public static Step Continue(string checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint);
        return new Step(false, checkpoint);
    }

    /// <summary>Commits this step's writes together with the job's completion.</summary>
    public static Step Complete() => new(true, null);
}

/// <summary>The outcome of a standard handler's attempt (§3.4).</summary>
public sealed class JobResult
{
    private JobResult(JobResultKind kind, TimeSpan? delay, string? reason)
    {
        Kind = kind;
        Delay = delay;
        Reason = reason;
    }

    /// <summary>What the handler asked for.</summary>
    public JobResultKind Kind { get; }

    /// <summary>The delay for <see cref="RetryAfter"/> and <see cref="Snooze"/>.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>The reason for a retry or a cancel.</summary>
    public string? Reason { get; }

    /// <summary>The job succeeded.</summary>
    public static JobResult Success() => new(JobResultKind.Success, null, null);

    /// <summary>Retry at an exact time; counts as an attempt.</summary>
    public static JobResult RetryAfter(TimeSpan delay, string reason) => new(JobResultKind.RetryAfter, delay, reason);

    /// <summary>Re-schedule without using an attempt (capped by MaxSnoozes and MaxJobAge).</summary>
    public static JobResult Snooze(TimeSpan delay) => new(JobResultKind.Snooze, delay, null);

    /// <summary>End the job as cancelled; it is never retried.</summary>
    public static JobResult Cancel(string reason) => new(JobResultKind.Cancel, null, reason);
}

/// <summary>The kinds of <see cref="JobResult"/>.</summary>
public enum JobResultKind
{
    /// <summary>Succeeded.</summary>
    Success = 1,

    /// <summary>Retry after a delay.</summary>
    RetryAfter = 2,

    /// <summary>Snooze without charging an attempt.</summary>
    Snooze = 3,

    /// <summary>Cancel.</summary>
    Cancel = 4,
}

/// <summary>Thrown by a handler for an error a retry cannot fix: the job is dead-lettered at once.</summary>
public sealed class PermanentJobException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PermanentJobException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PermanentJobException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public PermanentJobException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}