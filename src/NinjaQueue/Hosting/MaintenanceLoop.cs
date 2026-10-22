using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaQueue.Execution;
using NinjaQueue.Observability;
using NinjaQueue.Storage;

namespace NinjaQueue.Hosting;

/// <summary>
/// Maintenance (§7.4, §14.3, §14.6, §14.7, §16.3): each task runs only on the instance holding its named lease, which is an
/// optimization — every write the tasks make is conditional on its own row, so two leaders at once are harmless.
/// </summary>
internal sealed class MaintenanceLoop(
    IServiceScopeFactory scopes, NinjaQueueOptions options, JobMetrics metrics, string owner, Guid token, ILogger logger, WatchdogMonitor? watchdog = null)
{
    public const string Recovery = "maintenance.recovery";
    public const string Purge = "maintenance.purge";
    public const string Orphans = "maintenance.orphans";
    public const string Watchdog = "maintenance.watchdog";
    public const string Sampler = "metrics.sampler";
    public const string DeadLetters = "maintenance.dead_letters";

    public static readonly string[] LeaseNames = [Recovery, DeadLetters, Purge, Orphans, Watchdog, Sampler];

    private readonly RetryBackoff _backoff = new(options.Retry.BaseDelay, options.Retry.MaxDelay);

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await RunOnceAsync(stop);
            try
            {
                await Task.Delay(options.Worker.MaintenanceInterval, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One maintenance round: each task whose lease this instance holds.</summary>
    public async Task RunOnceAsync(CancellationToken stop)
    {
        TimeSpan ttl = options.Worker.MaintenanceInterval * 3;
        await RunTaskAsync(Recovery, ttl, s => s.RecoverExpiredLeasesAsync(100, attempt => _backoff.Next(attempt), stop), stop);

        // Right after recovery, so a lease-expiry dead letter at the last attempt gets its handler in the same round (§18 F7).
        await RunTaskAsync(DeadLetters, ttl, async _ =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<DeadLetterDispatcher>().SweepAsync(stop);
        }, stop);
        switch (await RunTaskAsync(Watchdog, ttl, s => s.KillOverdueFencedStepsAsync(TimeSpan.FromSeconds(30), stop), stop))
        {
            case TaskRun.Ran or TaskRun.NotHolder:
                watchdog?.Ok();
                break;
            case TaskRun.Failed:
                watchdog?.Failed();
                break;
        }

        await RunTaskAsync(Orphans, ttl, s => s.DetectOrphansAsync(options.Worker.UnknownTypeOrphanAfter, options.Worker.WorkerLivenessWindow, stop), stop);
        await RunTaskAsync(Purge, ttl, s => s.PurgeAsync(options.Retention, stop), stop);
        await RunTaskAsync(Sampler, ttl, async s =>
        {
            metrics.UpdateSamples(await s.SampleQueuesAsync(stop));
            return 0;
        }, stop);
    }

    private enum TaskRun
    {
        Stopped,
        NotHolder,
        Ran,
        Failed,
    }

    private async Task<TaskRun> RunTaskAsync(string name, TimeSpan ttl, Func<IJobMaintenanceStore, Task<int>> task, CancellationToken stop)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IJobMaintenanceStore store = scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>();
            if (!await store.TryAcquireLeaseAsync(name, owner, token, ttl, stop))
            {
                return TaskRun.NotHolder;
            }

            int count = await task(store);
            if (count > 0)
            {
                JobLog.MaintenanceDone(logger, name, count);
            }

            return TaskRun.Ran;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return TaskRun.Stopped;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            JobLog.MaintenanceFailed(logger, ex, name);
            return TaskRun.Failed;
        }
    }
}