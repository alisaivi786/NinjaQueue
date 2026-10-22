namespace NinjaQueue;

/// <summary>How one execution attempt ended (<c>job_attempts.outcome</c>). Permanent, append-only values.</summary>
public enum JobAttemptOutcome : short
{
    /// <summary>The handler completed the job.</summary>
    Succeeded = 1,

    /// <summary>The handler threw or asked for a retry.</summary>
    Failed = 2,

    /// <summary>The job type's timeout fired.</summary>
    TimedOut = 3,

    /// <summary>The worker lost (or could not confirm) its lease.</summary>
    LeaseLost = 4,

    /// <summary>The attempt was cancelled.</summary>
    Cancelled = 5,

    /// <summary>The handler snoozed the job; the attempt is refunded.</summary>
    Snoozed = 6,

    /// <summary>Released on graceful shutdown; the attempt is refunded.</summary>
    Released = 7,

    /// <summary>Deferred because the claimed job type was not registered; the attempt is refunded.</summary>
    Deferred = 8,
}

/// <summary>Why a job reached its terminal state (<c>background_jobs.terminal_reason_code</c>). Permanent, append-only values.</summary>
public enum JobTerminalReason : short
{
    /// <summary>The handler completed the job.</summary>
    Completed = 1,

    /// <summary>Every attempt was used up.</summary>
    Exhausted = 2,

    /// <summary>A permanent error (invalid payload, a permanent exception, ...).</summary>
    PermanentError = 3,

    /// <summary>An admin cancelled the job.</summary>
    AdminCancelled = 4,

    /// <summary>A continuation whose parent was dead-lettered.</summary>
    ParentFailed = 5,

    /// <summary>A continuation whose parent was cancelled.</summary>
    ParentCancelled = 6,

    /// <summary>The job outlived the maximum job age.</summary>
    MaxAgeExceeded = 7,

    /// <summary>The handler returned a cancel result.</summary>
    HandlerCancelled = 8,
}

/// <summary>When a continuation runs (<c>background_jobs.continuation_policy</c>). Permanent, append-only values.</summary>
public enum ContinuationPolicy : short
{
    /// <summary>Only after the parent succeeds; cancelled when the parent fails or is cancelled.</summary>
    OnParentSuccess = 1,

    /// <summary>After the parent ends in any terminal state.</summary>
    OnParentCompletion = 2,
}

/// <summary>What a recurring schedule does with occurrences missed during downtime (<c>job_recurring.misfire_policy</c>).
/// Permanent, append-only values.</summary>
public enum MisfirePolicy : short
{
    /// <summary>Enqueue only the latest missed occurrence.</summary>
    FireOnce = 1,

    /// <summary>Enqueue every missed occurrence, capped.</summary>
    FireAll = 2,

    /// <summary>Enqueue none; wait for the next occurrence.</summary>
    Skip = 3,
}

/// <summary>The state of one external operation in the side-effect ledger (<c>job_side_effects.status</c>). Permanent,
/// append-only values.</summary>
public enum SideEffectStatus : short
{
    /// <summary>A worker owns the step and may be issuing the call.</summary>
    Started = 1,

    /// <summary>The call is known to have succeeded.</summary>
    Completed = 2,

    /// <summary>The outcome of the call is unknown.</summary>
    Ambiguous = 3,

    /// <summary>The step was given up.</summary>
    Abandoned = 4,
}

/// <summary>The kind of a bulk admin operation (<c>job_admin_operations.operation</c>). Permanent, append-only values.</summary>
public enum AdminOperationKind : short
{
    /// <summary>Retry dead-lettered jobs.</summary>
    BulkRetry = 1,

    /// <summary>Requeue dead-lettered or cancelled jobs with a fresh attempt budget.</summary>
    BulkRequeue = 2,

    /// <summary>Cancel jobs.</summary>
    BulkCancel = 3,
}

/// <summary>The progress of a bulk admin operation (<c>job_admin_operations.status</c>). Permanent, append-only values.</summary>
public enum AdminOperationStatus : short
{
    /// <summary>Accepted, not started.</summary>
    Pending = 1,

    /// <summary>Being applied.</summary>
    Running = 2,

    /// <summary>Applied; the report is final.</summary>
    Completed = 3,

    /// <summary>Stopped by an error; the report covers the jobs processed so far.</summary>
    Failed = 4,
}