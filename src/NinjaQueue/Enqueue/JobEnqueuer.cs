using System.Diagnostics;
using NinjaQueue.Observability;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;
using NinjaQueue.Storage;

namespace NinjaQueue.Enqueue;

/// <summary>
/// Validates a job against its declared contract and hands it to the store (§7.1, §14.5, §15.3, §15.6), including the
/// Release 2 options: <see cref="JobOptions.ConcurrencyKey"/> (or the type's per-tenant slot group) and
/// <see cref="JobOptions.ContinueAfter"/>.
/// </summary>
internal sealed class JobEnqueuer(JobHandlerRegistry registry, JobPayloadSerializer serializer, IJobStore store, IJobSignal signal, JobMetrics metrics,
    IJobWakeupPublisher? wakeup = null)
    : IJobEnqueuer
{
    private const int MaxLabelLength = 100;

    public Task<JobId> StageAsync<TPayload>(TPayload payload, JobOptions? options = null, CancellationToken cancellationToken = default)
        where TPayload : notnull
        => StageCoreAsync(Build(payload, options), cancellationToken);

    public async Task<JobId> EnqueueAsync<TPayload>(TPayload payload, JobOptions? options = null, CancellationToken cancellationToken = default)
        where TPayload : notnull
    {
        NewJob job = Build(payload, options);
        IReadOnlyList<JobId> ids = await store.EnqueueAsync([job], cancellationToken);
        metrics.Enqueued(job.JobType);
        signal.Notify(job.Queue);
        await WakeAfterCommitAsync([job.Queue], cancellationToken);
        return ids[0];
    }

    public async Task<IReadOnlyList<JobId>> EnqueueManyAsync<TPayload>(
        IReadOnlyList<(TPayload Payload, JobOptions? Options)> jobs, CancellationToken cancellationToken = default)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return [];
        }

        NewJob[] built = [.. jobs.Select(j => Build(j.Payload, j.Options))];
        IReadOnlyList<JobId> ids = await store.EnqueueAsync(built, cancellationToken);
        foreach (NewJob job in built)
        {
            metrics.Enqueued(job.JobType);
        }

        foreach (string queue in built.Select(j => j.Queue).Distinct(StringComparer.Ordinal))
        {
            signal.Notify(queue);
        }

        await WakeAfterCommitAsync([.. built.Select(j => j.Queue).Distinct(StringComparer.Ordinal)], cancellationToken);
        return ids;
    }

    private async Task<JobId> StageCoreAsync(NewJob job, CancellationToken cancellationToken)
    {
        JobId id = await store.StageAsync(job, cancellationToken);

        // Counted when written into the caller's transaction: a caller that then rolls back is still counted (§7.6
        // counts enqueue calls; the claim and completion counters are the source of truth for work that ran).
        metrics.Enqueued(job.JobType);

        // The job is visible only after the caller commits; the signal just shortens the wait for the next poll.
        signal.Notify(job.Queue);

        // PostgreSQL NOTIFY in the caller's transaction (opt-in): delivered to other instances only if the caller commits.
        if (wakeup is not null)
        {
            await wakeup.PublishInTransactionAsync([job.Queue], cancellationToken);
        }

        return id;
    }

    // Best effort: the jobs are committed, so a failed wake-up only means other instances find them at their next poll.
    private async Task WakeAfterCommitAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken)
    {
        if (wakeup is null)
        {
            return;
        }

        try
        {
            await wakeup.PublishAsync(queues, cancellationToken);
        }
        catch (Exception ex) when (DatabaseFailures.IsDatabaseFailure(ex) || ex is TimeoutException)
        {
            // Polling picks the jobs up.
        }
    }

    internal NewJob Build<TPayload>(TPayload payload, JobOptions? options)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(payload);
        options ??= new JobOptions();

        if (!registry.TryGetByPayload(typeof(TPayload), out JobTypeRegistration? registration))
        {
            throw new InvalidOperationException(
                $"No job type is registered for payload {typeof(TPayload).FullName}. Register its handler with AddJobHandler<T>() or AddJobHandlersFromAssembly().");
        }

        string? concurrencyKey = ConcurrencyKeyFor(registration, options);
        if (options.ContinueAfter is { } parent && parent.Value == Guid.Empty)
        {
            throw new ArgumentException("ContinueAfter must be a job id.", nameof(options));
        }

        if (!Enum.IsDefined(options.ContinuationPolicy))
        {
            throw new ArgumentException($"ContinuationPolicy {options.ContinuationPolicy} is not valid.", nameof(options));
        }

        Guid tenantScope;
        switch (registration.Tenancy)
        {
            case JobTenancy.TenantOwned when options.TenantId is null || options.TenantId == Guid.Empty:
                throw new ArgumentException($"'{registration.Code}' is tenant-owned: JobOptions.TenantId is required (from the trusted request context).", nameof(options));
            case JobTenancy.TenantOwned:
                tenantScope = options.TenantId!.Value;
                break;
            case JobTenancy.System when options.TenantId is not null:
                throw new ArgumentException($"'{registration.Code}' is a system job type: it takes no TenantId.", nameof(options));
            default:
                tenantScope = JobLimits.SystemTenantScope;
                break;
        }

        string queue = options.Queue ?? registration.Queue;
        if (queue.Length is 0 or > JobLimits.MaxQueueLength)
        {
            throw new ArgumentException($"Queue '{queue}' is not valid.", nameof(options));
        }

        int maxAttempts = options.MaxAttempts ?? registration.MaxAttempts;
        if (maxAttempts is < 1 or > JobLimits.MaxConfiguredAttempts)
        {
            throw new ArgumentException($"MaxAttempts must be between 1 and {JobLimits.MaxConfiguredAttempts}.", nameof(options));
        }

        return new NewJob(
            Guid.NewGuid(),
            registration.Code,
            queue,
            options.TenantId,
            tenantScope,
            serializer.Serialize(payload, typeof(TPayload)),
            ValidKey(options.IdempotencyKey, nameof(JobOptions.IdempotencyKey)),
            options.OnIdempotencyConflict,
            ValidKey(options.UniqueKey, nameof(JobOptions.UniqueKey)),
            options.OnUniqueKeyConflict,
            maxAttempts,
            options.RunAt?.ToUniversalTime(),
            Label(options.CorrelationId ?? Activity.Current?.TraceId.ToString(), nameof(JobOptions.CorrelationId)),
            Label(options.CausationId, nameof(JobOptions.CausationId)),
            Activity.Current?.Id is { Length: <= MaxLabelLength } traceParent ? traceParent : null,
            options.ActorId,
            Label(options.Source, nameof(JobOptions.Source)))
        {
            ConcurrencyKey = concurrencyKey,
            ContinueAfter = options.ContinueAfter,
            ContinuationPolicy = options.ContinuationPolicy,
        };
    }

    // §14.5: the caller's key, or the type's per-tenant slot group. The two cannot be combined (one key column per job).
    private static string? ConcurrencyKeyFor(JobTypeRegistration registration, JobOptions options)
    {
        string? key = ValidKey(options.ConcurrencyKey, nameof(JobOptions.ConcurrencyKey));
        if (key is not null && key.StartsWith(ConcurrencyKeys.ReservedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"ConcurrencyKey must not start with the reserved prefix '{ConcurrencyKeys.ReservedPrefix}'.", nameof(options));
        }

        if (registration.MaxRunningPerTenant <= 0)
        {
            return key;
        }

        return key is null
            ? ConcurrencyKeys.SlotGroup(registration.Code, registration.MaxRunningPerTenant)
            : throw new ArgumentException(
                $"'{registration.Code}' declares MaxRunningPerTenant = {registration.MaxRunningPerTenant}; a job of this type cannot also carry a ConcurrencyKey.", nameof(options));
    }

    // Keys: ASCII, no whitespace, 1-200 characters, case-sensitive (§14.5).
    private static string? ValidKey(string? key, string name)
    {
        if (key is null)
        {
            return null;
        }

        return key.Length is 0 or > JobLimits.MaxKeyLength || key.Any(c => c is < '!' or > '~')
            ? throw new ArgumentException($"{name} must be 1-{JobLimits.MaxKeyLength} printable ASCII characters with no spaces, for example 'company:provision'.", name)
            : key;
    }

    private static string? Label(string? value, string name)
        => value is { Length: > MaxLabelLength } ? throw new ArgumentException($"{name} is longer than {MaxLabelLength} characters.", name) : value;
}

/// <summary>An in-process wake-up for queue workers: enqueue signals it so a worker does not wait for its next poll.</summary>
public interface IJobSignal
{
    /// <summary>Signals that <paramref name="queue"/> may have new work.</summary>
    void Notify(string queue);

    /// <summary>Waits for a signal on <paramref name="queue"/> or the timeout, whichever comes first.</summary>
    Task WaitAsync(string queue, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The default in-process <see cref="IJobSignal"/>.</summary>
internal sealed class InProcessJobSignal : IJobSignal
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);

    public void Notify(string queue)
    {
        SemaphoreSlim signal = _signals.GetOrAdd(queue, _ => new SemaphoreSlim(0, 1));
        if (signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another enqueue signalled first; one pending signal is enough.
            }
        }
    }

    public async Task WaitAsync(string queue, TimeSpan timeout, CancellationToken cancellationToken)
        => await _signals.GetOrAdd(queue, _ => new SemaphoreSlim(0, 1)).WaitAsync(timeout, cancellationToken);
}