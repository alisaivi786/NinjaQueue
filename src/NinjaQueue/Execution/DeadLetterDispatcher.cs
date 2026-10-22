using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;
using NinjaQueue.Storage;

namespace NinjaQueue.Execution;

/// <summary>
/// Runs <see cref="IJobDeadLetterHandler{TPayload}"/> for dead-lettered jobs (plan §18 F7), exactly once per job whichever
/// path dead-lettered it. Two triggers share one store call whose marker claim makes them safe to race: the executor right
/// after it commits a dead letter, and the maintenance sweep, which picks up every dead letter whose marker is still NULL
/// (lease-expiry recovery at the last attempt, a crash between the dead letter and its handler, a handler failure to retry).
/// A dead letter of a type this instance knows without a handler is settled (marker set, nothing else), so adding a handler
/// later never replays old dead letters. A type this instance does not know is left for an instance that does.
/// </summary>
public sealed class DeadLetterDispatcher(
    IServiceScopeFactory scopes, JobHandlerRegistry registry, IOptions<NinjaQueueOptions> options, ILogger<DeadLetterDispatcher> logger)
{
    /// <summary>Dead letters handled per sweep round.</summary>
    public const int SweepBatchSize = 100;

    private static readonly MethodInfo Invoker = typeof(DeadLetterDispatcher).GetMethod(nameof(Invoke), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<Type, Func<object, object?, JobDeadLetterContext, CancellationToken, Task>> Invokers = new();
    private readonly int _maxAttempts = Math.Max(1, options.Value.Retry.MaxDeadLetterHandlerAttempts);

    /// <summary>The executor's trigger, right after a dead letter committed. Never throws: the sweep retries what this misses.</summary>
    internal async Task AfterDeadLetterAsync(long jobId, string jobType)
    {
        try
        {
            await RunAsync(jobId, jobType, CancellationToken.None);
        }
#pragma warning disable CA1031 // Best effort: a failure here (database down, ...) leaves the marker NULL for the sweep.
        catch (Exception ex)
        {
            DeadLetterLog.TriggerFailed(logger, ex, jobType, jobId);
        }
#pragma warning restore CA1031
    }

    /// <summary>One sweep round: up to <see cref="SweepBatchSize"/> unhandled dead letters of known types. Returns the jobs it
    /// handled, settled or counted a failure for. Database failures propagate to the maintenance loop.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(long Id, string JobType)> pending;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            pending = await scope.ServiceProvider.GetRequiredService<IJobDeadLetterStore>()
                .PendingDeadLettersAsync(registry.Codes, _maxAttempts, SweepBatchSize, cancellationToken);
        }

        int processed = 0;
        foreach ((long id, string jobType) in pending)
        {
            DeadLetterRun run = await RunAsync(id, jobType, cancellationToken);
            if (run.Status != DeadLetterRunStatus.NotPending)
            {
                processed++;
            }
        }

        return processed;
    }

    /// <summary>Runs (or settles) the dead-letter handling of one job.</summary>
    public async Task<DeadLetterRun> RunAsync(long jobId, string jobType, CancellationToken cancellationToken)
    {
        if (!registry.TryGetByCode(jobType, out JobTypeRegistration? registration))
        {
            return new DeadLetterRun(DeadLetterRunStatus.NotPending);
        }

        TimeSpan timeout = registration.StepTimeout > JobHandlerRegistry.MaxStepTimeout ? JobHandlerRegistry.MaxStepTimeout : registration.StepTimeout;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IJobDeadLetterStore store = scope.ServiceProvider.GetRequiredService<IJobDeadLetterStore>();
        if (registration.DeadLetterHandlerType is null)
        {
            return await store.RunDeadLetterHandlerAsync(jobId, null, timeout, _maxAttempts, cancellationToken);
        }

        object handler = scope.ServiceProvider.GetRequiredService(registration.DeadLetterHandlerType);
        Func<object, object?, JobDeadLetterContext, CancellationToken, Task> invoke = InvokerFor(registration.PayloadType);
        bool unreadable = false;
        DeadLetterRun run = await store.RunDeadLetterHandlerAsync(jobId, (job, ct) =>
        {
            JobDeadLetterContext context = new(job.JobId, job.JobType, job.TenantId, job.Attempt, job.MaxAttempts, job.CorrelationId, job.ErrorCode, job.Error);
            object? payload = TryDeserialize(job.Payload, registration.PayloadType);
            unreadable = payload is null;
            return invoke(handler, payload, context, ct);
        }, timeout, _maxAttempts, cancellationToken);

        switch (run.Status)
        {
            case DeadLetterRunStatus.Handled when unreadable:
                DeadLetterLog.HandledWithoutPayload(logger, jobType, jobId);
                break;
            case DeadLetterRunStatus.Failed:
                DeadLetterLog.HandlerFailed(logger, run.Error, jobType, jobId, run.FailedAttempts, _maxAttempts);
                break;
            case DeadLetterRunStatus.Abandoned:
                DeadLetterLog.HandlerAbandoned(logger, run.Error, jobType, jobId, run.FailedAttempts);
                break;
        }

        return run;
    }

    private static object? TryDeserialize(string? payload, Type payloadType)
    {
        try
        {
            return JobPayloadSerializer.Deserialize(payload, payloadType);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static Func<object, object?, JobDeadLetterContext, CancellationToken, Task> InvokerFor(Type payloadType)
        => Invokers.GetOrAdd(payloadType, t => Invoker.MakeGenericMethod(t).CreateDelegate<Func<object, object?, JobDeadLetterContext, CancellationToken, Task>>());

    private static Task Invoke<TPayload>(object handler, object? payload, JobDeadLetterContext context, CancellationToken ct)
    {
        IJobDeadLetterHandler<TPayload> typed = (IJobDeadLetterHandler<TPayload>)handler;
        return payload is null ? typed.OnPayloadUnreadableAsync(context, ct) : typed.OnDeadLetteredAsync((TPayload)payload, context, ct);
    }
}

/// <summary>Source-generated dead-letter handler log messages. Payloads are never logged.</summary>
internal static partial class DeadLetterLog
{
    [LoggerMessage(EventId = 7120, Level = LogLevel.Warning, Message = "Dead-letter handler for job {JobType} ({JobId}) failed (attempt {Attempt} of {MaxAttempts}); the sweep retries it")]
    public static partial void HandlerFailed(ILogger logger, Exception? exception, string jobType, long jobId, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 7121, Level = LogLevel.Error, Message = "Dead-letter handler for job {JobType} ({JobId}) failed {Attempts} times and is abandoned")]
    public static partial void HandlerAbandoned(ILogger logger, Exception? exception, string jobType, long jobId, int attempts);

    [LoggerMessage(EventId = 7122, Level = LogLevel.Warning, Message = "Dead-letter handler for job {JobType} ({JobId}) could not run after the dead letter; the sweep retries it")]
    public static partial void TriggerFailed(ILogger logger, Exception exception, string jobType, long jobId);

    [LoggerMessage(EventId = 7123, Level = LogLevel.Warning, Message = "Dead letter of job {JobType} ({JobId}) handled without its payload, which could not be read")]
    public static partial void HandledWithoutPayload(ILogger logger, string jobType, long jobId);
}