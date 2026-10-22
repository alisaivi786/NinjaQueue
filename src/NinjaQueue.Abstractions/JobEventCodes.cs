namespace NinjaQueue;

/// <summary>
/// The canonical <c>job_events.event_code</c> values. Stable strings, append-only, at most <see cref="MaxLength"/>
/// characters. A code is never renamed or reused, because support queries and dashboards read them.
/// </summary>
public static class JobEventCodes
{
    /// <summary>The maximum length of an event code (<c>job_events.event_code</c>).</summary>
    public const int MaxLength = 50;

    // Lifecycle
    public const string JobEnqueued = nameof(JobEnqueued);
    public const string JobDeduplicated = nameof(JobDeduplicated);
    public const string JobClaimed = nameof(JobClaimed);
    public const string JobSucceeded = nameof(JobSucceeded);
    public const string JobRetryScheduled = nameof(JobRetryScheduled);
    public const string JobTimedOut = nameof(JobTimedOut);
    public const string JobCompletedAfterTimeout = nameof(JobCompletedAfterTimeout);
    public const string JobDeadLettered = nameof(JobDeadLettered);
    public const string JobMaxAgeExceeded = nameof(JobMaxAgeExceeded);

    // Dead-letter handlers (IJobDeadLetterHandler<T>)
    public const string JobDeadLetterHandled = nameof(JobDeadLetterHandled);
    public const string JobDeadLetterHandlerFailed = nameof(JobDeadLetterHandlerFailed);
    public const string JobDeadLetterHandlerAbandoned = nameof(JobDeadLetterHandlerAbandoned);

    // Leases
    public const string JobLeaseExpired = nameof(JobLeaseExpired);
    public const string JobLeaseLost = nameof(JobLeaseLost);
    public const string JobReleased = nameof(JobReleased);
    public const string JobSnoozed = nameof(JobSnoozed);
    public const string JobDeferred = nameof(JobDeferred);
    public const string FencedStepKilled = nameof(FencedStepKilled);

    // Cancellation
    public const string JobCancelRequested = nameof(JobCancelRequested);
    public const string JobCancelled = nameof(JobCancelled);
    public const string JobCancelTooLate = nameof(JobCancelTooLate);

    // Admin
    public const string JobRequeued = nameof(JobRequeued);
    public const string JobReplaced = nameof(JobReplaced);
    public const string BulkOperationApplied = nameof(BulkOperationApplied);

    // Rolling deploy
    public const string JobOrphaned = nameof(JobOrphaned);
    public const string JobOrphanCleared = nameof(JobOrphanCleared);

    // Continuations
    public const string ContinuationReleased = nameof(ContinuationReleased);
    public const string ContinuationCancelled = nameof(ContinuationCancelled);
    public const string ContinuationRestoreConflict = nameof(ContinuationRestoreConflict);

    // Recurring
    public const string RecurringSkippedOverlap = nameof(RecurringSkippedOverlap);
    public const string RecurringPaused = nameof(RecurringPaused);
    public const string RecurringResumed = nameof(RecurringResumed);
    public const string RecurringTriggered = nameof(RecurringTriggered);

    // Side effects
    public const string SideEffectUnknown = nameof(SideEffectUnknown);
    public const string SideEffectDuplicateRisk = nameof(SideEffectDuplicateRisk);

    // Guard / integrity
    public const string FencedContractViolation = nameof(FencedContractViolation);
    public const string JobStateConflict = nameof(JobStateConflict);

    /// <summary>Every canonical code.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        JobEnqueued, JobDeduplicated, JobClaimed, JobSucceeded, JobRetryScheduled, JobTimedOut, JobCompletedAfterTimeout,
        JobDeadLettered, JobMaxAgeExceeded,
        JobDeadLetterHandled, JobDeadLetterHandlerFailed, JobDeadLetterHandlerAbandoned,
        JobLeaseExpired, JobLeaseLost, JobReleased, JobSnoozed, JobDeferred, FencedStepKilled,
        JobCancelRequested, JobCancelled, JobCancelTooLate,
        JobRequeued, JobReplaced, BulkOperationApplied,
        JobOrphaned, JobOrphanCleared,
        ContinuationReleased, ContinuationCancelled, ContinuationRestoreConflict,
        RecurringSkippedOverlap, RecurringPaused, RecurringResumed, RecurringTriggered,
        SideEffectUnknown, SideEffectDuplicateRisk,
        FencedContractViolation, JobStateConflict,
    ];
}