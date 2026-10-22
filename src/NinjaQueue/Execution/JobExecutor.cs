using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Observability;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;
using NinjaQueue.Storage;

namespace NinjaQueue.Execution;

/// <summary>Why the executor's cancellation fired. Precedence when several fired: LeaseLost &gt; AdminCancel &gt; Timeout &gt; Shutdown (§14.1).</summary>
public sealed class JobSignals : IDisposable
{
    private readonly CancellationTokenSource _leaseLost = new();
    private readonly CancellationTokenSource _adminCancel = new();
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenSource _linked;

    /// <summary>Links the four sources with the host shutdown token.</summary>
    public JobSignals(CancellationToken shutdown)
    {
        Shutdown = shutdown;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _leaseLost.Token, _adminCancel.Token, _timeout.Token);
    }

    /// <summary>The token handed to the handler.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>The host shutdown token.</summary>
    public CancellationToken Shutdown { get; }

    /// <summary>True when the lease was lost or could not be confirmed.</summary>
    public bool LeaseLost => _leaseLost.IsCancellationRequested;

    /// <summary>True when an admin cancel was seen.</summary>
    public bool AdminCancel => _adminCancel.IsCancellationRequested;

    /// <summary>True when the timeout fired.</summary>
    public bool TimedOut => _timeout.IsCancellationRequested;

    /// <summary>Fires the lease-lost source.</summary>
    public void SignalLeaseLost() => Cancel(_leaseLost);

    /// <summary>
    /// False for a job type declared <c>Interruptible = false</c> (§14.4 step 6): a pending cancel then never fires the
    /// handler's token; it only stops future retries. Set by the executor before the handler starts.
    /// </summary>
    public bool Interruptible { get; set; } = true;

    /// <summary>Fires the admin-cancel source.</summary>
    public void SignalAdminCancel() => Cancel(_adminCancel);

    /// <summary>Arms the timeout.</summary>
    public void ArmTimeout(TimeSpan timeout) => _timeout.CancelAfter(timeout);

    /// <inheritdoc />
    public void Dispose()
    {
        _linked.Dispose();
        _leaseLost.Dispose();
        _adminCancel.Dispose();
        _timeout.Dispose();
    }

    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt already ended.
        }
    }
}

/// <summary>What happened to one claimed job.</summary>
public enum ExecutionResult
{
    /// <summary>Completed (Succeeded).</summary>
    Succeeded = 1,

    /// <summary>Rescheduled (retry, snooze, release or defer).</summary>
    Rescheduled = 2,

    /// <summary>Dead-lettered.</summary>
    DeadLettered = 3,

    /// <summary>Cancelled.</summary>
    Cancelled = 4,

    /// <summary>The lease was lost: nothing recorded by this worker; recovery owns the job.</summary>
    LeaseLost = 5,

    /// <summary>A state conflict was recorded; recovery and admin review own the job.</summary>
    StateConflict = 6,
}

/// <summary>
/// Runs one claimed job end to end (§4.3): a fresh DI scope per attempt (and per fenced step), payload from the registry
/// type, the handler, then exactly one outcome recorded — conditional on the lease — according to §15.7.
/// </summary>
public sealed class JobExecutor(
    IServiceScopeFactory scopes,
    JobHandlerRegistry registry,
    IOptions<NinjaQueueOptions> options,
    IRetryClassifier classifier,
    JobMetrics metrics,
    ILogger<JobExecutor> logger,
    DeadLetterDispatcher? deadLetters = null)
{
    private static readonly MethodInfo FencedInvoker = typeof(JobExecutor).GetMethod(nameof(InvokeFenced), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo StandardInvoker = typeof(JobExecutor).GetMethod(nameof(InvokeStandard), BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly NinjaQueueOptions _options = options.Value;
    private readonly RetryBackoff _backoff = new(options.Value.Retry.BaseDelay, options.Value.Retry.MaxDelay);

    /// <summary>Executes <paramref name="job"/>. <paramref name="signals"/> carries lease-loss/admin-cancel/timeout/shutdown.</summary>
    public async Task<ExecutionResult> ExecuteAsync(ClaimedJob job, JobSignals signals)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(signals);

        using Activity? activity = JobActivitySource.StartExecute(job);
        using IDisposable? logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["job_id"] = job.JobId.Value,
            ["job_type"] = job.JobType,
            ["tenant_id"] = job.TenantId,
            ["attempt"] = job.Attempt,
            ["worker_id"] = job.WorkerId,
            ["correlation_id"] = job.CorrelationId,
        });
        long started = Stopwatch.GetTimestamp();
        metrics.InFlight(+1);
        try
        {
            ExecutionResult result = await ExecuteCoreAsync(job, signals);
            metrics.Completed(job.JobType, result, Stopwatch.GetElapsedTime(started));
            activity?.SetTag("ninjaqueue.outcome", result.ToString());
            JobLog.Finished(logger, job.JobType, result, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        finally
        {
            metrics.InFlight(-1);
        }
    }

    private async Task<ExecutionResult> ExecuteCoreAsync(ClaimedJob job, JobSignals signals)
    {
        if (!registry.TryGetByCode(job.JobType, out JobTypeRegistration? registration))
        {
            // Safety net (§14.3): a claimed code this registry does not know is deferred, never dead-lettered.
            return await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Deferred, EventCode: JobEventCodes.JobDeferred));
        }

        object payload;
        try
        {
            payload = JobPayloadSerializer.Deserialize(job.Payload, registration.PayloadType);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            return await RecordAsync(job, Failure(AttemptOutcomeKind.DeadLetter, "InvalidPayload", ex));
        }

        return registration.IsFenced
            ? await ExecuteFencedAsync(job, registration, payload, signals)
            : await ExecuteStandardAsync(job, registration, payload, signals);
    }

    // ---- path A: fenced steps (§15.1) ------------------------------------------------------------------------------

    private async Task<ExecutionResult> ExecuteFencedAsync(ClaimedJob job, JobTypeRegistration registration, object payload, JobSignals signals)
    {
        FencedStepSettings settings = new(_options.Worker.LeaseDuration, registration.StepTimeout, registration.Interruptible);
        Func<object, object, FencedJobContext, CancellationToken, Task<Step>> invoke = FencedInvokerFor(registration.PayloadType);
        ClaimedJob current = job;

        while (true)
        {
            if (signals.Shutdown.IsCancellationRequested)
            {
                // Between steps the job holds no transaction: release it (refunded) instead of starting another step.
                return await RecordAsync(current, new AttemptOutcome(AttemptOutcomeKind.Released));
            }

            FencedStepOutcome outcome;
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                IJobStore store = scope.ServiceProvider.GetRequiredService<IJobStore>();
                object handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
                using CancellationTokenSource stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(signals.Token);
                stepTimeout.CancelAfter(registration.StepTimeout);
                FencedJobContext context = null!;
                try
                {
                    outcome = await store.RunFencedStepAsync(current, settings, async (checkpoint, ct) =>
                    {
                        context = new FencedJobContext(current.JobId, current.JobType, current.TenantId, current.Attempt, current.MaxAttempts, current.CorrelationId, checkpoint);
                        Step step = await invoke(handler, payload, context, ct);
                        return new FencedStepResult(step.IsComplete, step.Checkpoint, context.Progress?.Processed, context.Progress?.Total);
                    }, stepTimeout.Token);
                }
                catch (OperationCanceledException) when (stepTimeout.IsCancellationRequested)
                {
                    outcome = new FencedStepOutcome(FencedStepStatus.Failed, Error: new TimeoutException("The fenced step was cancelled."));
                }
                catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex))
                {
                    // The step transaction itself failed (connection killed by the server-side bound, deadlock, ...), raw
                    // or wrapped by Npgsql's execution strategy. The store reports most of these as Failed already.
                    outcome = new FencedStepOutcome(FencedStepStatus.Failed, Error: ex);
                }

                if (outcome.Status == FencedStepStatus.Failed && stepTimeout.IsCancellationRequested && !signals.Token.IsCancellationRequested)
                {
                    outcome = outcome with { Error = new TimeoutException($"The fenced step exceeded its {registration.StepTimeout} timeout.", outcome.Error) };
                    return await RecordAsync(current, Failure(AttemptOutcomeKind.TimedOut, "Timeout", outcome.Error!, current.Attempt));
                }
            }

            switch (outcome.Status)
            {
                case FencedStepStatus.Continued:
                    current = current with { Checkpoint = outcome.Checkpoint };
                    continue;
                case FencedStepStatus.Completed:
                    return ExecutionResult.Succeeded;
                case FencedStepStatus.Cancelled:
                    return ExecutionResult.Cancelled;
                case FencedStepStatus.LeaseLost or FencedStepStatus.OwnershipUnconfirmed:
                    signals.SignalLeaseLost();
                    JobLog.LeaseLost(logger, current.JobType);
                    metrics.LeaseLost(current.JobType);
                    return ExecutionResult.LeaseLost;
                case FencedStepStatus.StateConflict:
                    return ExecutionResult.StateConflict;
                default:
                    return await RecordFailureAsync(current, outcome.Error!, signals);
            }
        }
    }

    // ---- path B: standard (§15.1, §15.7 rule 3) --------------------------------------------------------------------

    private async Task<ExecutionResult> ExecuteStandardAsync(ClaimedJob job, JobTypeRegistration registration, object payload, JobSignals signals)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IJobStore store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        object handler = scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        Func<object, object, JobContext, CancellationToken, Task<JobResult>> invoke = StandardInvokerFor(registration.PayloadType);
        SideEffectLedger? ledger = registration.UsesLedger ? new SideEffectLedger(scopes, job, registration.OnAmbiguous, signals) : null;
        JobContext context = new(job.JobId, job.JobType, job.TenantId, job.Attempt, job.MaxAttempts, job.CorrelationId,
            async (processed, total, ct) =>
            {
                if (!await store.ReportProgressAsync(job, processed, total, _options.Worker.LeaseDuration, ct))
                {
                    signals.SignalLeaseLost();
                    throw new OperationCanceledException("The job's lease was lost.", signals.Token);
                }
            },
            ledger);

        signals.Interruptible = registration.Interruptible;
        signals.ArmTimeout(registration.Timeout);
        JobResult result;
        try
        {
            result = await invoke(handler, payload, context, signals.Token);
        }
        catch (OperationCanceledException) when (signals.Token.IsCancellationRequested)
        {
            if (signals.LeaseLost)
            {
                metrics.LeaseLost(job.JobType);
                return ExecutionResult.LeaseLost;
            }

            return signals.AdminCancel
                ? await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Cancelled, TerminalReason: JobTerminalReason.AdminCancelled))
                : signals.TimedOut
                    ? await RecordAsync(job, Failure(AttemptOutcomeKind.TimedOut, "Timeout", new TimeoutException($"The job exceeded its {registration.Timeout} timeout."), job.Attempt))
                    : await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Released));
        }
#pragma warning disable CA1031 // The handler boundary: any handler exception becomes a recorded attempt outcome.
        catch (Exception ex)
        {
            if (signals.LeaseLost)
            {
                return ExecutionResult.LeaseLost;
            }

            if (ledger?.HaltOutcome is { } halted)
            {
                return await RecordAsync(job, halted);
            }

            return await RecordFailureAsync(job, ex, signals);
        }
#pragma warning restore CA1031

        if (signals.LeaseLost)
        {
            return ExecutionResult.LeaseLost;
        }

        // An ambiguous ledger step ends the attempt by its policy even if the handler swallowed the halt (§15.5).
        if (ledger?.HaltOutcome is { } halt)
        {
            return await RecordAsync(job, halt);
        }

        return result.Kind switch
        {
            // The work was done: a late success is recorded truthfully (§15.7 rule 3).
            JobResultKind.Success => await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Succeeded,
                EventCode: signals.TimedOut ? JobEventCodes.JobCompletedAfterTimeout : null)),
            JobResultKind.RetryAfter => await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Failed, result.Delay, "RetryAfter", result.Reason)),
            JobResultKind.Snooze => await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Snoozed, Clamp(result.Delay ?? TimeSpan.FromSeconds(1)))),
            JobResultKind.Cancel => await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Cancelled, Error: result.Reason, TerminalReason: JobTerminalReason.HandlerCancelled)),
            _ => throw new InvalidOperationException($"Unknown result {result.Kind}."),
        };
    }

    private async Task<ExecutionResult> RecordFailureAsync(ClaimedJob job, Exception error, JobSignals signals)
    {
        if (error is OperationCanceledException && signals.Shutdown.IsCancellationRequested && !signals.AdminCancel && !signals.TimedOut)
        {
            return await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Released));
        }

        if (error is OperationCanceledException && signals.AdminCancel)
        {
            return await RecordAsync(job, new AttemptOutcome(AttemptOutcomeKind.Cancelled, TerminalReason: JobTerminalReason.AdminCancelled));
        }

        JobLog.HandlerFailed(logger, error, job.JobType);
        return classifier.IsPermanent(error)
            ? await RecordAsync(job, Failure(AttemptOutcomeKind.DeadLetter, "Permanent", error))
            : await RecordAsync(job, Failure(AttemptOutcomeKind.Failed, ErrorCode(error), error, job.Attempt));
    }

    private AttemptOutcome Failure(AttemptOutcomeKind kind, string code, Exception error, int? attemptForBackoff = null)
        => new(kind, attemptForBackoff is { } attempt ? _backoff.Next(attempt) : null, code, Message(error), error.GetType().FullName,
            TerminalReason: kind == AttemptOutcomeKind.DeadLetter ? JobTerminalReason.PermanentError : null);

    private async Task<ExecutionResult> RecordAsync(ClaimedJob job, AttemptOutcome outcome)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        OutcomeResult recorded = await scope.ServiceProvider.GetRequiredService<IJobStore>().RecordOutcomeAsync(job, outcome, CancellationToken.None);
        if (outcome.Kind is AttemptOutcomeKind.Failed or AttemptOutcomeKind.TimedOut && recorded == OutcomeResult.Recorded)
        {
            metrics.Retried(job.JobType);
        }

        ExecutionResult result = recorded switch
        {
            OutcomeResult.LeaseLost => ExecutionResult.LeaseLost,
            OutcomeResult.DeadLettered => DeadLettered(job),
            OutcomeResult.Cancelled => ExecutionResult.Cancelled,
            _ => outcome.Kind switch
            {
                AttemptOutcomeKind.Succeeded => ExecutionResult.Succeeded,
                AttemptOutcomeKind.DeadLetter => DeadLettered(job),
                AttemptOutcomeKind.Cancelled => ExecutionResult.Cancelled,
                _ => ExecutionResult.Rescheduled,
            },
        };

        // The dead letter has committed: run its handler now (exactly once, marker-guarded; the sweep covers a miss).
        if (result == ExecutionResult.DeadLettered && deadLetters is not null)
        {
            await deadLetters.AfterDeadLetterAsync(job.Id, job.JobType);
        }

        return result;
    }

    private ExecutionResult DeadLettered(ClaimedJob job)
    {
        metrics.DeadLettered(job.JobType);
        return ExecutionResult.DeadLettered;
    }

    private static TimeSpan Clamp(TimeSpan delay) => delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : delay;

    private static string ErrorCode(Exception error) => error switch
    {
        TimeoutException => "Timeout",
        HttpRequestException => "Http",
        _ when DatabaseFailures.IsDatabaseFailure(error) => "Database",
        _ => "Exception",
    };

    // Errors are stored truncated and without stack traces; the full exception goes to the logs only (§11).
    private static string Message(Exception error) => error.Message.Length > 2000 ? error.Message[..2000] : error.Message;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Delegate> Invokers = new();

    private static Func<object, object, FencedJobContext, CancellationToken, Task<Step>> FencedInvokerFor(Type payloadType)
        => (Func<object, object, FencedJobContext, CancellationToken, Task<Step>>)Invokers.GetOrAdd(payloadType,
            t => FencedInvoker.MakeGenericMethod(t).CreateDelegate<Func<object, object, FencedJobContext, CancellationToken, Task<Step>>>());

    private static Func<object, object, JobContext, CancellationToken, Task<JobResult>> StandardInvokerFor(Type payloadType)
        => (Func<object, object, JobContext, CancellationToken, Task<JobResult>>)Invokers.GetOrAdd(typeof(StandardKey<>).MakeGenericType(payloadType),
            _ => StandardInvoker.MakeGenericMethod(payloadType).CreateDelegate<Func<object, object, JobContext, CancellationToken, Task<JobResult>>>());

    private static Task<Step> InvokeFenced<TPayload>(object handler, object payload, FencedJobContext context, CancellationToken ct)
        => ((IFencedJobHandler<TPayload>)handler).HandleStepAsync((TPayload)payload, context, ct);

    private static Task<JobResult> InvokeStandard<TPayload>(object handler, object payload, JobContext context, CancellationToken ct)
        => ((IJobHandler<TPayload>)handler).HandleAsync((TPayload)payload, context, ct);

    private static class StandardKey<T>;
}