using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.Execution;
using NinjaQueue.Observability;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.Hosting;

/// <summary>
/// Runs the queue workers, heartbeat and maintenance inside the host (§10 of the TL;DR, §7.2, §16.2). Stops in the single
/// shutdown budget: stop claiming, drain, cancel (fenced steps roll back; cooperative handlers are released and refunded),
/// abandon the rest (their leases expire), then clean up.
/// </summary>
public sealed class NinjaQueueWorkerHost : IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly JobHandlerRegistry _registry;
    private readonly JobExecutor _executor;
    private readonly IJobSignal _signal;
    private readonly JobMetrics _metrics;
    private readonly NinjaQueueOptions _options;
    private readonly HostOptions _hostOptions;
    private readonly ILogger<NinjaQueueWorkerHost> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();
    private readonly CancellationTokenSource _stopClaiming = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _stopBackground = new();
    private readonly List<Task> _loops = [];
    private readonly Guid _leaseToken = Guid.NewGuid();

    /// <summary>Creates the host.</summary>
    public NinjaQueueWorkerHost(
        IServiceScopeFactory scopes, JobHandlerRegistry registry, JobExecutor executor, IJobSignal signal, JobMetrics metrics,
        IOptions<NinjaQueueOptions> options, IOptions<HostOptions> hostOptions, ILogger<NinjaQueueWorkerHost> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostOptions);
        _scopes = scopes;
        _registry = registry;
        _executor = executor;
        _signal = signal;
        _metrics = metrics;
        _options = options.Value;
        _hostOptions = hostOptions.Value;
        _logger = logger;
        WorkerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
    }

    /// <summary>This instance's worker id (lease owner).</summary>
    public string WorkerId { get; }

    /// <summary>The time of the last successful heartbeat, read by the health check.</summary>
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }

    /// <summary>True while claims are failing (database outage), read by the health check.</summary>
    public bool ClaimsFailing { get; private set; }

    /// <summary>The watchdog's reachability as seen by this instance's maintenance loop, read by the health check.</summary>
    internal WatchdogMonitor Watchdog { get; } = new(TimeProvider.System);

    /// <summary>Jobs running in this process.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateShutdownBudget();

        // An enqueue-only instance (workers off, e.g. an API whose jobs another service runs) never needs the database to
        // start: it may come up before the database or the instance that owns the schema. The server checks run with workers.
        if (!_options.Worker.Enabled)
        {
            return;
        }

        await StartupChecks.RunAsync(_scopes, _registry, _options, cancellationToken);

        await HeartbeatOnceAsync(cancellationToken);
        foreach ((string queue, int concurrency) in _options.Worker.Queues)
        {
            IReadOnlyList<string> codes = _registry.CodesForQueue(queue);
            if (codes.Count > 0 && concurrency > 0)
            {
                _loops.Add(Task.Run(() => QueueLoopAsync(queue, concurrency, codes), CancellationToken.None));
            }
        }

        _loops.Add(Task.Run(HeartbeatLoopAsync, CancellationToken.None));
        _loops.Add(Task.Run(() => new MaintenanceLoop(_scopes, _options, _metrics, WorkerId, _leaseToken, _logger, Watchdog).RunAsync(_stopBackground.Token), CancellationToken.None));
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stage 0: stop claiming.
        await _stopClaiming.CancelAsync();

        // Stage 1: drain; handlers keep running and renewing.
        await WaitInFlightAsync(_options.Worker.ShutdownDrainPeriod);

        // Stage 2: cancel; fenced steps roll back, cooperative handlers are released (refunded).
        await _shutdown.CancelAsync();
        await WaitInFlightAsync(_options.Worker.ShutdownCancelPeriod);

        // Stage 3: abandon whatever still runs; its lease is neither renewed nor cleared, so it expires (charged).
        // Stage 4: cleanup within its own window.
        await _stopBackground.CancelAsync();
        using CancellationTokenSource cleanup = new(_options.Worker.ShutdownCleanupPeriod);
        try
        {
            await Task.WhenAll(_loops).WaitAsync(cleanup.Token);
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            IJobMaintenanceStore store = scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>();
            foreach (string name in MaintenanceLoop.LeaseNames)
            {
                await store.ReleaseLeaseAsync(name, _leaseToken, cleanup.Token);
            }

            await store.RemoveWorkerAsync(WorkerId, _options.Worker.WorkerLivenessWindow * 5, cleanup.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or DbException or TimeoutException)
        {
            JobLog.MaintenanceFailed(_logger, ex, "shutdown-cleanup");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stopClaiming.Dispose();
        _shutdown.Dispose();
        _stopBackground.Dispose();
        return ValueTask.CompletedTask;
    }

    private void ValidateShutdownBudget()
    {
        TimeSpan needed = _options.Worker.ShutdownDrainPeriod + _options.Worker.ShutdownCancelPeriod + _options.Worker.ShutdownCleanupPeriod + TimeSpan.FromSeconds(3);
        if (needed > _hostOptions.ShutdownTimeout)
        {
            throw new InvalidOperationException(
                $"NinjaQueue shutdown budget (drain + cancel + cleanup + 3 s = {needed}) exceeds HostOptions.ShutdownTimeout ({_hostOptions.ShutdownTimeout}) (§16.2).");
        }

        // The renewal interval is (lease - margin) / 3, so LeaseDuration >= 3 x interval + margin holds by construction (§14.2);
        // the margin must leave room for at least one renewal.
        if (_options.Worker.LeaseSafetyMargin <= TimeSpan.Zero || _options.Worker.LeaseSafetyMargin >= _options.Worker.LeaseDuration / 2)
        {
            throw new InvalidOperationException("LeaseSafetyMargin must be positive and below half of LeaseDuration (§14.2).");
        }
    }

    private async Task QueueLoopAsync(string queue, int concurrency, IReadOnlyList<string> codes)
    {
        using SemaphoreSlim slots = new(concurrency, concurrency);
        TimeSpan failureDelay = TimeSpan.FromSeconds(1);
        CancellationToken stop = _stopClaiming.Token;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await slots.WaitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int free = 1;
            while (free < concurrency && slots.Wait(0))
            {
                free++;
            }

            IReadOnlyList<ClaimedJob> claimed;
            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                claimed = await scope.ServiceProvider.GetRequiredService<IJobStore>()
                    .ClaimAsync(queue, codes, WorkerId, free, _options.Worker.LeaseDuration, stop);
                ClaimsFailing = false;
                failureDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                slots.Release(free);
                break;
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                // A database outage never kills the worker: back off 1 s → 60 s and report Degraded.
                slots.Release(free);
                ClaimsFailing = true;
                JobLog.ClaimFailed(_logger, ex, queue, (long)failureDelay.TotalMilliseconds);
                await DelayAsync(failureDelay, stop);
                failureDelay = TimeSpan.FromMilliseconds(Math.Min(60_000, failureDelay.TotalMilliseconds * 2));
                continue;
            }

            if (claimed.Count < free)
            {
                slots.Release(free - claimed.Count);
            }

            foreach (ClaimedJob job in claimed)
            {
                _metrics.Claimed(job.JobType);
                _inFlight[job.JobId.Value] = RunJobAsync(job, slots);
            }

            if (claimed.Count == 0)
            {
                await WaitForWorkAsync(queue, stop);
            }
        }
    }

    private async Task RunJobAsync(ClaimedJob job, SemaphoreSlim slots)
    {
        await Task.Yield();
        using JobSignals signals = new(_shutdown.Token);
        using CancellationTokenSource attemptEnded = new();

        // Shutdown stage 3 (§16.2): an abandoned handler's lease is neither renewed nor cleared, so renewal also stops there.
        using CancellationTokenSource renewUntil = CancellationTokenSource.CreateLinkedTokenSource(attemptEnded.Token, _stopBackground.Token);
        Task renewal = Task.CompletedTask;
        try
        {
            if (_registry.TryGetByCode(job.JobType, out JobTypeRegistration? registration) && !registration.IsFenced)
            {
                // The renewer must not inherit the executor's ambient unit of work: start it with a clean execution context.
                using (ExecutionContext.SuppressFlow())
                {
                    renewal = Task.Run(() => new LeaseRenewer(_scopes, _options, _logger).RunAsync(job, signals, renewUntil.Token), CancellationToken.None);
                }
            }

            await _executor.ExecuteAsync(job, signals);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            // The outcome could not be recorded (database down): the lease expires and recovery owns the job.
            JobLog.HandlerFailed(_logger, ex, job.JobType);
        }
        finally
        {
            await attemptEnded.CancelAsync();
            await renewal;
            _inFlight.TryRemove(job.JobId.Value, out _);
            slots.Release();
        }
    }

    private async Task WaitForWorkAsync(string queue, CancellationToken stop)
    {
        try
        {
            await _signal.WaitAsync(queue, _options.Worker.PollInterval, stop);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_stopBackground.IsCancellationRequested)
        {
            await DelayAsync(TimeSpan.FromSeconds(15), _stopBackground.Token);
            if (_stopBackground.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await HeartbeatOnceAsync(_stopBackground.Token);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                JobLog.MaintenanceFailed(_logger, ex, "heartbeat");
            }
        }
    }

    private async Task HeartbeatOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>().HeartbeatAsync(new WorkerRegistration(
            WorkerId, Environment.MachineName, Environment.ProcessId, typeof(NinjaQueueWorkerHost).Assembly.GetName().Version?.ToString(),
            [.. _options.Worker.Queues.Keys], _registry.Codes), cancellationToken);
        LastHeartbeatUtc = DateTimeOffset.UtcNow;
    }

    private async Task WaitInFlightAsync(TimeSpan window)
    {
        Task all = Task.WhenAll(_inFlight.Values.ToArray());
        await Task.WhenAny(all, Task.Delay(window));
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}