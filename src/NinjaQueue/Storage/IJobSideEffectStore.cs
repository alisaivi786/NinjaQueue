namespace NinjaQueue.Storage;

/// <summary>
/// Storage of the external operation ledger, <c>job_side_effects</c> (§15.5). Statements that start or take over a step are
/// lease-checked in the same short transaction; statements that record what a call did are conditional on the step's owner
/// token, so a worker that lost its lease can still record a truthful result unless another worker took the step over.
/// </summary>
public interface IJobSideEffectStore
{
    /// <summary>Lease check, then inserts the step as Started and owned by the job's lease token, or returns the existing row.</summary>
    Task<SideEffectBegin> BeginSideEffectAsync(ClaimedJob job, string stepKey, CancellationToken cancellationToken);

    /// <summary>
    /// Lease check, then compare-and-set on the step's owner: when it is still <paramref name="expectedOwner"/>, sets
    /// <paramref name="status"/>, the owner to <paramref name="newOwner"/>, the result fields when given, and adds one to
    /// <c>issue_count</c> when <paramref name="countIssue"/>. Writes <paramref name="eventCode"/> (with <paramref name="eventData"/>)
    /// in the same transaction when the swap wins.
    /// </summary>
    Task<SideEffectSwap> SwapSideEffectAsync(
        ClaimedJob job, string stepKey, Guid expectedOwner, Guid newOwner, SideEffectStatus status, bool countIssue, string? externalRef,
        string? resultJson, string? eventCode, string? eventData, CancellationToken cancellationToken);

    /// <summary>
    /// Records what this worker's call did (Completed, Ambiguous or Abandoned), only while the job's lease token still owns
    /// the step. When it no longer does, writes <paramref name="lostOwnerEventCode"/> (when given) and returns false.
    /// </summary>
    Task<bool> RecordSideEffectAsync(
        ClaimedJob job, string stepKey, SideEffectStatus status, string? externalRef, string? resultJson, string? lostOwnerEventCode, string? eventData,
        CancellationToken cancellationToken);
}

/// <summary>The row a <see cref="IJobSideEffectStore.BeginSideEffectAsync"/> found or created.</summary>
public sealed record SideEffectBegin(SideEffectBeginKind Kind, SideEffectStatus Status, Guid Owner, int IssueCount, string? ExternalRef, string? ResultJson)
{
    /// <summary>The lease is gone.</summary>
    public static SideEffectBegin LeaseLost { get; } = new(SideEffectBeginKind.LeaseLost, SideEffectStatus.Started, Guid.Empty, 0, null, null);
}

/// <summary>What <see cref="IJobSideEffectStore.BeginSideEffectAsync"/> did.</summary>
public enum SideEffectBeginKind
{
    /// <summary>The lease was lost: nothing written.</summary>
    LeaseLost = 1,

    /// <summary>This worker now owns a new step.</summary>
    Inserted = 2,

    /// <summary>The step already existed.</summary>
    Existing = 3,
}

/// <summary>The result of a <see cref="IJobSideEffectStore.SwapSideEffectAsync"/>.</summary>
public enum SideEffectSwapResult
{
    /// <summary>The lease was lost: nothing written.</summary>
    LeaseLost = 1,

    /// <summary>The swap won.</summary>
    Won = 2,

    /// <summary>Another owner holds the step now.</summary>
    Lost = 3,
}

/// <summary>A swap's result and the step's issue count after it.</summary>
public readonly record struct SideEffectSwap(SideEffectSwapResult Result, int IssueCount);