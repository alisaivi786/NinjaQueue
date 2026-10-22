namespace NinjaQueue;

/// <summary>
/// What the external operation ledger does when an earlier attempt (or a stale worker) started a call whose outcome is
/// unknown (§15.5, §14.9). A previously started call is never issued again blindly.
/// </summary>
public enum SideEffectAmbiguity
{
    /// <summary>Not declared. The registry refuses an <see cref="JobSideEffects.ExternalLedger"/> type without a policy.</summary>
    Unspecified = 0,

    /// <summary>Take the step over and issue the call again (a duplicate is better than a loss), up to
    /// <see cref="SideEffectOptions.MaxIssues"/> calls in total; then <see cref="MarkUnknown"/>. Every re-issue is recorded as a
    /// <c>SideEffectDuplicateRisk</c> event.</summary>
    Retry = 1,

    /// <summary>Record the step as ambiguous and finish the job as Succeeded with a <c>SideEffectUnknown</c> event.</summary>
    MarkUnknown = 2,

    /// <summary>Record the step as ambiguous and dead-letter the job, so an operator decides.</summary>
    DeadLetter = 3,
}

/// <summary>What an external call (or a reconcile lookup) reports to the ledger.</summary>
public sealed class SideEffectOutcome
{
    private SideEffectOutcome(SideEffectOutcomeKind kind, string? externalRef, string? resultJson, string? reason)
    {
        Kind = kind;
        ExternalRef = externalRef;
        ResultJson = resultJson;
        Reason = reason;
    }

    /// <summary>The kind of outcome.</summary>
    public SideEffectOutcomeKind Kind { get; }

    /// <summary>The provider's reference for a completed call (message id, server response, ...), at most 500 characters.</summary>
    public string? ExternalRef { get; }

    /// <summary>A small JSON result to store with a completed call and return to later attempts.</summary>
    public string? ResultJson { get; }

    /// <summary>Why the call was not issued or is ambiguous (truncated, no PII).</summary>
    public string? Reason { get; }

    /// <summary>The provider accepted the call.</summary>
    public static SideEffectOutcome Completed(string? externalRef = null, string? resultJson = null) => new(SideEffectOutcomeKind.Completed, externalRef, resultJson, null);

    /// <summary>The call was definitely not issued (DNS, connect, TLS, auth, a 4xx before the payload): safe to issue again.
    /// From a reconcile lookup: the provider has no record of it.</summary>
    public static SideEffectOutcome NotIssued(string reason) => new(SideEffectOutcomeKind.NotIssued, null, null, reason);

    /// <summary>The call may or may not have taken effect (a timeout or connection loss after the payload was sent).</summary>
    public static SideEffectOutcome Ambiguous(string reason) => new(SideEffectOutcomeKind.Ambiguous, null, null, reason);
}

/// <summary>The kinds of <see cref="SideEffectOutcome"/>.</summary>
public enum SideEffectOutcomeKind
{
    /// <summary>The call took effect.</summary>
    Completed = 1,

    /// <summary>The call did not take effect.</summary>
    NotIssued = 2,

    /// <summary>Unknown.</summary>
    Ambiguous = 3,
}

/// <summary>Per-call overrides for <see cref="JobContext.SideEffectAsync"/>.</summary>
public sealed record SideEffectOptions
{
    /// <summary>Overrides the job type's <see cref="JobTypeAttribute.OnAmbiguous"/> (for example per email category, §14.9).</summary>
    public SideEffectAmbiguity? OnAmbiguous { get; init; }

    /// <summary>The most calls <see cref="SideEffectAmbiguity.Retry"/> may issue for one step, the first included (default 2:
    /// one extra send).</summary>
    public int MaxIssues { get; init; } = 2;
}

/// <summary>The result of a completed ledger step.</summary>
/// <param name="ExternalRef">The provider reference stored with the step.</param>
/// <param name="ResultJson">The JSON result stored with the step.</param>
/// <param name="Reused">True when an earlier attempt already completed the step and nothing was issued now.</param>
/// <param name="IssueCount">How many times the call has been issued for this step.</param>
/// <param name="DuplicateRisk">True when the call may have been issued more than once (recorded as <c>SideEffectDuplicateRisk</c>).</param>
public sealed record SideEffectResult(string? ExternalRef, string? ResultJson, bool Reused, int IssueCount, bool DuplicateRisk);

/// <summary>
/// The engine's side of <see cref="JobContext.SideEffectAsync"/>; implemented by NinjaQueue, not by applications.
/// </summary>
public interface IJobSideEffectLedger
{
    /// <summary>Runs one ledger step (§15.5).</summary>
    Task<SideEffectResult> RunAsync(
        string stepKey,
        Func<string, CancellationToken, Task<SideEffectOutcome>> issue,
        Func<string, CancellationToken, Task<SideEffectOutcome>>? reconcile,
        SideEffectOptions? options,
        CancellationToken cancellationToken);
}

/// <summary>
/// An external call was definitely not issued (<see cref="SideEffectOutcome.NotIssued"/>). Retryable: the job is retried
/// with backoff and the next attempt issues the call again.
/// </summary>
public sealed class SideEffectNotIssuedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SideEffectNotIssuedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectNotIssuedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectNotIssuedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An external call's outcome is unknown and the policy is <see cref="SideEffectAmbiguity.Retry"/>. Retryable: the next
/// attempt takes the step over and issues the call again while the issue limit allows.
/// </summary>
public sealed class SideEffectAmbiguousException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SideEffectAmbiguousException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectAmbiguousException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectAmbiguousException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Ends the attempt because a ledger step is ambiguous and the policy is <see cref="SideEffectAmbiguity.MarkUnknown"/> or
/// <see cref="SideEffectAmbiguity.DeadLetter"/>. Do not catch it: the engine applies the policy even if a handler swallows it.
/// </summary>
public sealed class SideEffectHaltException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SideEffectHaltException(string stepKey, SideEffectAmbiguity policy, string? reason)
        : base($"Side effect '{stepKey}' has an unknown outcome; policy {policy}.")
    {
        StepKey = stepKey;
        Policy = policy;
        Reason = reason;
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectHaltException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectHaltException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public SideEffectHaltException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The ambiguous step.</summary>
    public string StepKey { get; } = "";

    /// <summary>The policy applied.</summary>
    public SideEffectAmbiguity Policy { get; }

    /// <summary>Why the step is ambiguous.</summary>
    public string? Reason { get; }
}

/// <summary>Marks a payload property the admin API always masks, even for callers allowed to see payloads (§17.8).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveAttribute : Attribute;