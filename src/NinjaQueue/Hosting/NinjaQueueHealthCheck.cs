using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NinjaQueue.Observability;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.Hosting;

/// <summary>
/// The <c>ninjaqueue</c> health check (§7.6, §16.3): Unhealthy when the database cannot be reached, or when the fenced-step
/// watchdog has not completed a round for more than three maintenance intervals (<c>watchdog_unreachable</c>); Degraded when
/// claims are failing, the heartbeat is stale while workers are enabled, a queue's oldest ready job waits too long, or fenced
/// jobs run without the watchdog's kill permission (either provider). Reports RCSI, compatibility level and the shutdown budget.
/// </summary>
public sealed class NinjaQueueHealthCheck(
    IServiceScopeFactory scopes, JobHandlerRegistry registry, JobMetrics metrics, IOptions<NinjaQueueOptions> options, IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        NinjaQueueOptions o = options.Value;
        Dictionary<string, object> data = new(StringComparer.Ordinal)
        {
            ["shutdown_budget_s"] = (o.Worker.ShutdownDrainPeriod + o.Worker.ShutdownCancelPeriod + o.Worker.ShutdownCleanupPeriod).TotalSeconds,
        };

        ServerCheck check;
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            check = await scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>().CheckServerAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("The jobs database cannot be reached.", ex, data);
        }

        data["provider"] = check.IsPostgres ? "PostgreSQL" : "SqlServer";
        data[check.IsPostgres ? "server_version_num" : "compatibility_level"] = check.VersionOrCompatibilityLevel;
        if (!check.IsPostgres)
        {
            data["is_read_committed_snapshot_on"] = check.ReadCommittedSnapshot;
        }

        List<string> degraded = [];
        NinjaQueueWorkerHost? host = services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<NinjaQueueWorkerHost>().FirstOrDefault();
        if (o.Worker.Enabled && host is not null)
        {
            data["worker_id"] = host.WorkerId;
            data["in_flight"] = host.InFlightCount;
            if (host.ClaimsFailing)
            {
                degraded.Add("claims are failing");
            }

            if (host.LastHeartbeatUtc is null || DateTimeOffset.UtcNow - host.LastHeartbeatUtc > TimeSpan.FromMinutes(1))
            {
                degraded.Add("worker heartbeat is stale");
            }
        }

        foreach (QueueSample sample in metrics.Samples.Where(s => s.OldestPendingAge > o.OldestPendingDegradedAfter))
        {
            degraded.Add($"queue '{sample.Queue}' oldest ready job waited {sample.OldestPendingAge.TotalSeconds:F0} s");
        }

        // LISTEN/NOTIFY wake-up (Release 2): enabled on PostgreSQL with workers but not listening (connecting, or reconnecting
        // after a killed backend or network loss). Polling still runs every queue, so jobs are only slower: Degraded, not Unhealthy.
        if (o.ListenNotify.Enabled && o.Worker.Enabled && check.IsPostgres && services.GetService<IJobWakeupListener>() is { } listener)
        {
            data["listen_notify_listening"] = listener.IsListening;
            if (!listener.IsListening)
            {
                degraded.Add("LISTEN/NOTIFY wake-up is not listening; workers fall back to polling");
            }
        }

        bool hasFenced = registry.All.Any(r => r.IsFenced);
        data["watchdog_can_kill"] = check.CanKillSessions;

        // Both providers: without the kill permission (SQL Server ALTER ANY CONNECTION; PostgreSQL superuser or pg_signal_backend)
        // the watchdog cannot end a hung fenced transaction. PostgreSQL stays bounded by its server-side timeouts and SQL Server
        // only by the row lock (§16.3), so both report the reduced guarantee as Degraded.
        if (!check.CanKillSessions && hasFenced)
        {
            degraded.Add("fenced jobs run without the watchdog (reduced guarantee)");
        }

        // watchdog_unreachable (§16.3): this instance's maintenance rounds could not reach the database or its session views for
        // more than three maintenance intervals. Only meaningful where fenced types exist and this instance runs maintenance.
        if (hasFenced && o.Worker.Enabled && host is not null && host.Watchdog.IsUnreachable(o.Worker.MaintenanceInterval * 3))
        {
            data["watchdog_last_ok_utc"] = host.Watchdog.LastOkUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "never";
            return HealthCheckResult.Unhealthy("watchdog_unreachable: the fenced-step watchdog cannot reach the database or its session views.", null, data);
        }

        return degraded.Count == 0
            ? HealthCheckResult.Healthy("NinjaQueue is healthy.", data)
            : HealthCheckResult.Degraded(string.Join("; ", degraded), null, data);
    }
}