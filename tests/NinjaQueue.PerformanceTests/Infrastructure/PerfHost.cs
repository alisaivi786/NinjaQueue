using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NinjaQueue.Hosting;
using NinjaQueue.Storage;

namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>
/// One application instance: its own DI container, its own connection pool (a distinct application name), its own
/// <see cref="NinjaQueueWorkerHost"/> (queue workers, heartbeat, maintenance) and its own in-process enqueue signal, wired
/// exactly as a host wires the engine (<c>AddDbContextFactory</c> + <c>AddNinjaQueue().UseEfCore()</c>).
/// Several instances share one database the way several API instances share production.
/// </summary>
public sealed class PerfHost : IAsyncDisposable
{
    private bool _workersStarted;
    private List<Microsoft.Extensions.Hosting.IHostedService> _hosted = [];

    private PerfHost(PerfDatabase database, string name, ServiceProvider services)
    {
        Database = database;
        Name = name;
        Services = services;
    }

    public PerfDatabase Database { get; }

    /// <summary>The instance name, also its database session name (application_name / program_name).</summary>
    public string Name { get; }

    public ServiceProvider Services { get; }

    public NinjaQueueWorkerHost Worker => Services.GetRequiredService<NinjaQueueWorkerHost>();

    /// <summary>Builds (does not start) an instance named <paramref name="name"/> on <paramref name="db"/>.</summary>
    public static PerfHost Build(PerfDatabase db, string name, Action<NinjaQueueOptions>? configure = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        string provider = db.IsPostgres ? "Postgres" : "SqlServer";
        string connectionString = db.ConnectionStringFor(name);
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Database:Provider"] = provider,
            [$"ConnectionStrings:{provider}Write"] = connectionString,
            [$"ConnectionStrings:{provider}ReadOnly"] = connectionString,
        };
        foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        ServiceCollection services = [];
        services.AddSingleton(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContextFactory<PerfDbContext>(options =>
        {
            if (db.IsPostgres)
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<PerfDbContext>>().CreateDbContext());
        services.AddScoped<PerfWidgetProvider>();
        services.AddNinjaQueue(configuration, o =>
        {
            o.Worker.Queues = new(StringComparer.Ordinal) { ["default"] = 16 };
            o.Worker.ShutdownDrainPeriod = TimeSpan.FromSeconds(5);
            o.Worker.ShutdownCancelPeriod = TimeSpan.FromSeconds(3);
            o.Worker.ShutdownCleanupPeriod = TimeSpan.FromSeconds(3);
            o.Retry.BaseDelay = TimeSpan.FromMilliseconds(10);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(50);
            configure?.Invoke(o);
        })
            .UsePerfDatabase(db)
            .AddJobHandler<NoOpHandler>()
            .AddJobHandler<LatencyHandler>()
            .AddJobHandler<FencedHandler>();
        return new PerfHost(db, name, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task<JobId> EnqueueAsync<TPayload>(TPayload payload, JobOptions? options = null)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().EnqueueAsync(payload, options));

    public Task<IReadOnlyList<JobId>> EnqueueManyAsync<TPayload>(IReadOnlyList<TPayload> payloads)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().EnqueueManyAsync<TPayload>([.. payloads.Select(p => (p, (JobOptions?)null))]));

    /// <summary>The real batch claim (§15.2) through the store, in its own scope.</summary>
    public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string type, int maxJobs, string queue = "default", string? worker = null)
        => InScopeAsync(sp => sp.GetRequiredService<IJobStore>().ClaimAsync(queue, [type], worker ?? Name, maxJobs, TimeSpan.FromMinutes(5), CancellationToken.None));

    /// <summary>Records a successful outcome for a claimed standard job (the executor's success path), in its own scope.</summary>
    public Task<OutcomeResult> SucceedAsync(ClaimedJob job)
        => InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RecordOutcomeAsync(job, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None));

    /// <summary>
    /// Starts every hosted service the engine registered, in order, as the generic host would: the registry validator, the
    /// worker host (startup checks, heartbeat, queue loops, maintenance) and the optional ones (LISTEN/NOTIFY listener,
    /// recurring scheduler).
    /// </summary>
    public async Task StartWorkersAsync()
    {
        _hosted = [.. Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()];
        foreach (Microsoft.Extensions.Hosting.IHostedService service in _hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        _workersStarted = true;
    }

    public async Task StopWorkersAsync()
    {
        if (_workersStarted)
        {
            _workersStarted = false;
            foreach (Microsoft.Extensions.Hosting.IHostedService service in Enumerable.Reverse(_hosted))
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopWorkersAsync();
        await Services.DisposeAsync();
    }

    /// <summary>Disposes every host in <paramref name="hosts"/>.</summary>
    public static async Task DisposeAllAsync(IEnumerable<PerfHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        foreach (PerfHost host in hosts)
        {
            await host.DisposeAsync();
        }
    }
}