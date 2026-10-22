using Microsoft.Extensions.DependencyInjection;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.Hosting;

/// <summary>
/// Refuses to start on a database the guarantees do not hold on (§15.2, §16.3, §17.6): SQL Server below compatibility level
/// 130 (OPENJSON in the claim), PostgreSQL below 13, or SQL Server fenced jobs without the watchdog's kill permission (unless
/// explicitly allowed, which leaves the health check Degraded). PostgreSQL without the kill permission (superuser or
/// <c>pg_signal_backend</c>) still starts, because its server-side timeouts bound a fenced transaction (§16.3); the health check
/// reports it Degraded, the same as the SQL Server opt-out.
/// </summary>
internal static class StartupChecks
{
    public const int MinSqlServerCompatibilityLevel = 130;
    public const int MinPostgresVersionNum = 130_000;

    public static async Task<ServerCheck> RunAsync(IServiceScopeFactory scopes, JobHandlerRegistry registry, NinjaQueueOptions options, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ServerCheck check = await scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>().CheckServerAsync(cancellationToken);
        Validate(check, registry.All.Any(r => r.IsFenced), options);
        return check;
    }

    public static void Validate(ServerCheck check, bool hasFencedJobs, NinjaQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(options);
        if (check.IsPostgres && check.VersionOrCompatibilityLevel < MinPostgresVersionNum)
        {
            throw new InvalidOperationException($"NinjaQueue needs PostgreSQL 13 or later (server_version_num {check.VersionOrCompatibilityLevel}).");
        }

        if (!check.IsPostgres && check.VersionOrCompatibilityLevel < MinSqlServerCompatibilityLevel)
        {
            throw new InvalidOperationException(
                $"NinjaQueue needs database compatibility level {MinSqlServerCompatibilityLevel} or higher (current {check.VersionOrCompatibilityLevel}). " +
                $"Run: ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = {MinSqlServerCompatibilityLevel};");
        }

        if (!check.IsPostgres && hasFencedJobs && !check.CanKillSessions && !options.Watchdog.AllowFencedWithoutWatchdog)
        {
            throw new InvalidOperationException(
                "Fenced job types on SQL Server need the watchdog's ALTER ANY CONNECTION permission (§16.3). Grant it, or set " +
                "NinjaQueue:Watchdog:AllowFencedWithoutWatchdog = true to run with a reduced guarantee (health Degraded).");
        }
    }
}