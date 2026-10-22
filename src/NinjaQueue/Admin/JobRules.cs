namespace NinjaQueue.Admin;

/// <summary>What a continuation does when its parent's state is known (§15.6).</summary>
public enum ContinuationDecision
{
    /// <summary>The parent is not terminal: the child waits as Awaiting, with no queue row.</summary>
    Await = 1,

    /// <summary>The child is released: Enqueued (or Scheduled) with a queue row.</summary>
    Release = 2,

    /// <summary>The child is cancelled (ParentFailed / ParentCancelled).</summary>
    Cancel = 3,
}

/// <summary>The continuation rules of §15.6, shared by the enqueue path and the parent's outcome transaction.</summary>
public static class ContinuationRules
{
    /// <summary>The child's fate for a parent in <paramref name="parentStatus"/>.</summary>
    public static ContinuationDecision Decide(JobStatus parentStatus, ContinuationPolicy policy)
        => !parentStatus.IsTerminal()
            ? ContinuationDecision.Await
            : parentStatus == JobStatus.Succeeded || policy == ContinuationPolicy.OnParentCompletion
                ? ContinuationDecision.Release
                : ContinuationDecision.Cancel;

    /// <summary>Why a child is cancelled when its parent ended in <paramref name="parentStatus"/>.</summary>
    public static JobTerminalReason CancelReason(JobStatus parentStatus)
        => parentStatus == JobStatus.DeadLettered ? JobTerminalReason.ParentFailed : JobTerminalReason.ParentCancelled;

    /// <summary>True for the terminal reasons an admin Retry/Requeue of the parent restores (§15.6).</summary>
    public static bool IsRestorable(JobTerminalReason? reason) => reason is JobTerminalReason.ParentFailed or JobTerminalReason.ParentCancelled;
}

/// <summary>The kinds of admin re-run (§15.11).</summary>
public enum AdminRunMode
{
    /// <summary>One more attempt now: DeadLettered only, <c>max_attempts = attempt + 1</c>.</summary>
    Retry = 1,

    /// <summary>A fresh attempt budget, now or later: DeadLettered or Cancelled, <c>max_attempts = attempt + type default</c>.</summary>
    Requeue = 2,
}

/// <summary>The pure admin Retry/Requeue rules (§15.11, G6).</summary>
public static class AdminRules
{
    /// <summary>True when <paramref name="mode"/> may run a job in <paramref name="status"/>. Succeeded is never re-run.</summary>
    public static bool IsEligible(AdminRunMode mode, JobStatus status)
        => mode == AdminRunMode.Retry ? status == JobStatus.DeadLettered : status is JobStatus.DeadLettered or JobStatus.Cancelled;

    /// <summary>
    /// The new <c>max_attempts</c>: attempt + 1 (Retry) or attempt + <paramref name="typeDefault"/> capped at the persisted
    /// ceiling (Requeue); null when even one more attempt would exceed <see cref="JobLimits.MaxPersistedAttempts"/>.
    /// </summary>
    public static int? NewMaxAttempts(AdminRunMode mode, int attempt, int typeDefault)
    {
        if (attempt + 1 > JobLimits.MaxPersistedAttempts)
        {
            return null;
        }

        return mode == AdminRunMode.Retry ? attempt + 1 : Math.Min(JobLimits.MaxPersistedAttempts, attempt + Math.Max(1, typeDefault));
    }
}