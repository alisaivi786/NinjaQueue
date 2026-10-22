using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NinjaQueue.Storage;

namespace NinjaQueue.Execution;

/// <summary>What the ledger does with a step it finds already in <c>job_side_effects</c> (§15.5 step 1).</summary>
public enum SideEffectDecision
{
    /// <summary>Completed earlier: return the stored result, issue nothing.</summary>
    Reuse = 1,

    /// <summary>Take the step over (compare-and-set on its owner) and issue the call.</summary>
    TakeOverAndIssue = 2,

    /// <summary>Mark the step Ambiguous and finish the job as Succeeded with <c>SideEffectUnknown</c>.</summary>
    MarkUnknown = 3,

    /// <summary>Mark the step Ambiguous and dead-letter the job.</summary>
    DeadLetter = 4,
}

/// <summary>The pure decision rules of the ledger protocol (§15.5), unit-tested over every state.</summary>
public static class SideEffectRules
{
    /// <summary>The decision for an existing step when no reconcile lookup answered.</summary>
    public static SideEffectDecision OnExisting(SideEffectStatus status, int issueCount, SideEffectAmbiguity policy, int maxIssues)
        => status switch
        {
            SideEffectStatus.Completed => SideEffectDecision.Reuse,

            // Abandoned = a worker recorded that its call was definitely not issued: safe to issue.
            SideEffectStatus.Abandoned => SideEffectDecision.TakeOverAndIssue,
            _ => policy switch
            {
                SideEffectAmbiguity.Retry when issueCount < maxIssues => SideEffectDecision.TakeOverAndIssue,
                SideEffectAmbiguity.DeadLetter => SideEffectDecision.DeadLetter,

                // MarkUnknown, and Retry once its issue budget is used up.
                _ => SideEffectDecision.MarkUnknown,
            },
        };

    /// <summary>The decision for a call of this attempt that ended ambiguously (§15.5 step 4).</summary>
    public static SideEffectDecision OnAmbiguousCall(SideEffectAmbiguity policy, int issueCount, int maxIssues)
        => policy switch
        {
            // Retry happens on the next attempt (with backoff), through the existing-step rules.
            SideEffectAmbiguity.Retry when issueCount < maxIssues => SideEffectDecision.TakeOverAndIssue,
            SideEffectAmbiguity.DeadLetter => SideEffectDecision.DeadLetter,
            _ => SideEffectDecision.MarkUnknown,
        };
}

/// <summary>
/// Runs <see cref="JobContext.SideEffectAsync"/> for one attempt of an <see cref="JobSideEffects.ExternalLedger"/> job
/// (§15.5): begin (lease-checked), issue outside any transaction, complete (owner-checked). Each database call uses its own
/// DI scope, so no transaction is ever open while the external call runs.
/// </summary>
internal sealed class SideEffectLedger(IServiceScopeFactory scopes, ClaimedJob job, SideEffectAmbiguity declaredPolicy, JobSignals signals) : IJobSideEffectLedger
{
    private const int MaxStepKeyLength = 200;

    /// <summary>Set when a step ended the attempt by its policy; the executor records it even if the handler swallowed the halt.</summary>
    public AttemptOutcome? HaltOutcome { get; private set; }

    public async Task<SideEffectResult> RunAsync(
        string stepKey,
        Func<string, CancellationToken, Task<SideEffectOutcome>> issue,
        Func<string, CancellationToken, Task<SideEffectOutcome>>? reconcile,
        SideEffectOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentNullException.ThrowIfNull(issue);
        if (stepKey.Length > MaxStepKeyLength)
        {
            throw new ArgumentException($"A side-effect step key is at most {MaxStepKeyLength} characters.", nameof(stepKey));
        }

        if (HaltOutcome is not null)
        {
            throw new InvalidOperationException("A previous side-effect step already ended this attempt.");
        }

        SideEffectAmbiguity policy = options?.OnAmbiguous ?? declaredPolicy;
        int maxIssues = Math.Max(1, options?.MaxIssues ?? 2);
        string operationKey = $"{job.JobId.Value:N}:{stepKey}";

        SideEffectBegin begin = await StoreAsync(s => s.BeginSideEffectAsync(job, stepKey, cancellationToken));
        if (begin.Kind == SideEffectBeginKind.LeaseLost)
        {
            throw LeaseLost();
        }

        int issueCount = 1;
        bool duplicateRisk = false;
        if (begin.Kind == SideEffectBeginKind.Existing)
        {
            if (begin.Status == SideEffectStatus.Completed)
            {
                return new SideEffectResult(begin.ExternalRef, begin.ResultJson, Reused: true, begin.IssueCount, DuplicateRisk: begin.IssueCount > 1);
            }

            bool unknown = begin.Status is SideEffectStatus.Started or SideEffectStatus.Ambiguous;
            if (unknown && reconcile is not null)
            {
                SideEffectOutcome? found = await TryReconcileAsync(reconcile, operationKey, cancellationToken);
                if (found?.Kind == SideEffectOutcomeKind.Completed)
                {
                    SideEffectSwap resolved = await SwapAsync(stepKey, begin.Owner, job.LeaseToken, SideEffectStatus.Completed, countIssue: false,
                        found.ExternalRef, found.ResultJson, null, null, cancellationToken);
                    return new SideEffectResult(found.ExternalRef, found.ResultJson, Reused: true, resolved.IssueCount, DuplicateRisk: false);
                }

                if (found?.Kind == SideEffectOutcomeKind.NotIssued)
                {
                    // The provider confirms it never received the call: a takeover cannot duplicate it.
                    issueCount = (await SwapAsync(stepKey, begin.Owner, job.LeaseToken, SideEffectStatus.Started, countIssue: true,
                        null, null, null, null, cancellationToken)).IssueCount;
                    return await IssueAsync(stepKey, operationKey, issue, policy, maxIssues, issueCount, duplicateRisk: false, cancellationToken);
                }
            }

            switch (SideEffectRules.OnExisting(begin.Status, begin.IssueCount, policy, maxIssues))
            {
                case SideEffectDecision.TakeOverAndIssue:
                    duplicateRisk = unknown;
                    issueCount = (await SwapAsync(stepKey, begin.Owner, job.LeaseToken, SideEffectStatus.Started, countIssue: true, null, null,
                        unknown ? JobEventCodes.SideEffectDuplicateRisk : null,
                        unknown ? EventData(stepKey, begin.IssueCount + 1, "takeover") : null, cancellationToken)).IssueCount;
                    break;
                case SideEffectDecision.DeadLetter:
                    await SwapAsync(stepKey, begin.Owner, begin.Owner, SideEffectStatus.Ambiguous, countIssue: false, null, null, null, null, cancellationToken);
                    throw Halt(stepKey, SideEffectAmbiguity.DeadLetter, "An earlier attempt started the call and its outcome is unknown.");
                default:
                    // The owner is kept: if the stale worker completes late, its truthful result still lands.
                    await SwapAsync(stepKey, begin.Owner, begin.Owner, SideEffectStatus.Ambiguous, countIssue: false, null, null, null, null, cancellationToken);
                    throw Halt(stepKey, SideEffectAmbiguity.MarkUnknown, "An earlier attempt started the call and its outcome is unknown.");
            }
        }

        return await IssueAsync(stepKey, operationKey, issue, policy, maxIssues, issueCount, duplicateRisk, cancellationToken);
    }

    private async Task<SideEffectResult> IssueAsync(
        string stepKey, string operationKey, Func<string, CancellationToken, Task<SideEffectOutcome>> issue, SideEffectAmbiguity policy, int maxIssues,
        int issueCount, bool duplicateRisk, CancellationToken cancellationToken)
    {
        SideEffectOutcome outcome;
        try
        {
            outcome = await issue(operationKey, cancellationToken) ?? SideEffectOutcome.Ambiguous("The call returned no outcome.");
        }
        catch (PermanentJobException)
        {
            // Definitely not issued and never to be retried: the step is safe to issue again only by a deliberate admin retry.
            await RecordAsync(stepKey, SideEffectStatus.Abandoned, null, null);
            throw;
        }
#pragma warning disable CA1031 // Any other failure of the call leaves its outcome unknown: the ledger never guesses.
        catch (Exception ex)
        {
            await RecordAsync(stepKey, SideEffectStatus.Ambiguous, null, null);
            if (ex is OperationCanceledException && signals.Token.IsCancellationRequested)
            {
                // Lease loss, admin cancel, timeout or shutdown: the engine's normal arbitration applies; the next attempt
                // finds the step Ambiguous and applies the policy.
                throw;
            }

            return OnAmbiguous(stepKey, policy, maxIssues, issueCount, ex.Message, ex);
        }
#pragma warning restore CA1031

        switch (outcome.Kind)
        {
            case SideEffectOutcomeKind.Completed:
                bool owned = await RecordAsync(stepKey, SideEffectStatus.Completed, Truncate(outcome.ExternalRef, 500), outcome.ResultJson,
                    JobEventCodes.SideEffectDuplicateRisk, EventData(stepKey, issueCount, "completed-after-takeover"));
                return new SideEffectResult(outcome.ExternalRef, outcome.ResultJson, Reused: false, issueCount, DuplicateRisk: duplicateRisk || !owned);
            case SideEffectOutcomeKind.NotIssued:
                await RecordAsync(stepKey, SideEffectStatus.Abandoned, null, null);
                throw new SideEffectNotIssuedException($"Side effect '{stepKey}' was not issued: {outcome.Reason}");
            default:
                await RecordAsync(stepKey, SideEffectStatus.Ambiguous, null, null);
                return OnAmbiguous(stepKey, policy, maxIssues, issueCount, outcome.Reason, null);
        }
    }

    private SideEffectResult OnAmbiguous(string stepKey, SideEffectAmbiguity policy, int maxIssues, int issueCount, string? reason, Exception? inner)
        => SideEffectRules.OnAmbiguousCall(policy, issueCount, maxIssues) switch
        {
            SideEffectDecision.TakeOverAndIssue => throw new SideEffectAmbiguousException($"Side effect '{stepKey}' has an unknown outcome; it is retried. {reason}", inner!),
            SideEffectDecision.DeadLetter => throw Halt(stepKey, SideEffectAmbiguity.DeadLetter, reason),
            _ => throw Halt(stepKey, SideEffectAmbiguity.MarkUnknown, reason),
        };

    private SideEffectHaltException Halt(string stepKey, SideEffectAmbiguity policy, string? reason)
    {
        string data = EventData(stepKey, null, reason);
        HaltOutcome = policy == SideEffectAmbiguity.DeadLetter
            ? new AttemptOutcome(AttemptOutcomeKind.DeadLetter, ErrorCode: "SideEffectAmbiguous", Error: Truncate($"Side effect '{stepKey}' has an unknown outcome. {reason}", 2000),
                TerminalReason: JobTerminalReason.PermanentError)
            : new AttemptOutcome(AttemptOutcomeKind.Succeeded, ErrorCode: "SideEffectUnknown", Error: data, EventCode: JobEventCodes.SideEffectUnknown);
        return new SideEffectHaltException(stepKey, policy, reason);
    }

    private async Task<SideEffectOutcome?> TryReconcileAsync(
        Func<string, CancellationToken, Task<SideEffectOutcome>> reconcile, string operationKey, CancellationToken cancellationToken)
    {
        try
        {
            return await reconcile(operationKey, cancellationToken);
        }
        catch (OperationCanceledException) when (signals.Token.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed lookup answers nothing: the declared policy decides.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<SideEffectSwap> SwapAsync(
        string stepKey, Guid expectedOwner, Guid newOwner, SideEffectStatus status, bool countIssue, string? externalRef, string? resultJson,
        string? eventCode, string? eventData, CancellationToken cancellationToken)
    {
        SideEffectSwap swap = await StoreAsync(s => s.SwapSideEffectAsync(job, stepKey, expectedOwner, newOwner, status, countIssue,
            Truncate(externalRef, 500), resultJson, eventCode, eventData, cancellationToken));
        return swap.Result switch
        {
            SideEffectSwapResult.Won => swap,
            SideEffectSwapResult.LeaseLost => throw LeaseLost(),

            // Exactly one takeover wins; the loser aborts the attempt (its lease is almost certainly gone).
            _ => throw LeaseLost(),
        };
    }

    // Never cancelled by the handler's token: a result that happened must be recorded even while the attempt is ending.
    private Task<bool> RecordAsync(string stepKey, SideEffectStatus status, string? externalRef, string? resultJson, string? lostOwnerEvent = null, string? data = null)
        => StoreAsync(s => s.RecordSideEffectAsync(job, stepKey, status, externalRef, resultJson, lostOwnerEvent, data, CancellationToken.None));

    private async Task<T> StoreAsync<T>(Func<IJobSideEffectStore, Task<T>> work)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<IJobSideEffectStore>());
    }

    private OperationCanceledException LeaseLost()
    {
        signals.SignalLeaseLost();
        return new OperationCanceledException("The job's lease was lost while running a side-effect step.", signals.Token);
    }

    private static string EventData(string stepKey, int? issueCount, string? note)
        => Truncate(JsonSerializer.Serialize(new Dictionary<string, object?> { ["step"] = stepKey, ["issue_count"] = issueCount, ["note"] = note }), 4000)!;

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}