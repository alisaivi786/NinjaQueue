namespace NinjaQueue;

/// <summary>
/// The persisted state of a job (<c>background_jobs.status</c>). Values are permanent and append-only: never renumber or
/// reuse one. 5–9 are reserved for future non-terminal states and 13–19 for future terminal states, so "terminal" is
/// always <c>status &gt;= 10</c> (<see cref="JobStatusExtensions.IsTerminal"/>).
/// </summary>
public enum JobStatus : short
{
    /// <summary>Ready to be claimed once <c>available_at_utc</c> is reached.</summary>
    Enqueued = 1,

    /// <summary>Waiting for a retry, snooze or requested start time.</summary>
    Scheduled = 2,

    /// <summary>A continuation waiting for its parent; it has no queue row and cannot be claimed.</summary>
    Awaiting = 3,

    /// <summary>Claimed by a worker that holds its lease.</summary>
    Processing = 4,

    /// <summary>Terminal: completed successfully.</summary>
    Succeeded = 10,

    /// <summary>Terminal: cancelled by an admin, the handler or a parent.</summary>
    Cancelled = 11,

    /// <summary>Terminal: failed permanently or used up its attempts.</summary>
    DeadLettered = 12,
}

/// <summary>Helpers over <see cref="JobStatus"/>.</summary>
public static class JobStatusExtensions
{
    /// <summary>The first terminal value. Every status at or above it is terminal.</summary>
    public const short FirstTerminalValue = 10;

    /// <summary>True for Succeeded, Cancelled, DeadLettered and any future terminal state.</summary>
    public static bool IsTerminal(this JobStatus status) => (short)status >= FirstTerminalValue;
}