using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.Execution;
using NinjaQueue.Hosting;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// The Phase 4 host: the engine wired exactly as <see cref="JobsHost"/> wires it, but on a database the test chooses, so
/// several hosts (workers) can share one database and a database can be created at a given SQL Server compatibility level.
/// Every handler in this folder is registered; a test only enqueues the types it needs.
/// </summary>
public sealed class Phase4Host : IAsyncDisposable
{
    private bool _workersStarted;

    private Phase4Host(TestDatabase database, ServiceProvider services)
    {
        Database = database;
        Services = services;
    }

    public TestDatabase Database { get; }

    public ServiceProvider Services { get; }

    public Phase4Probe Probe => Get<Phase4Probe>();

    public FaultPlan Faults => Get<FaultPlan>();

    /// <summary>Creates a migrated database with the business table.</summary>
    public static async Task<TestDatabase> CreateDatabaseAsync(JobsDatabaseFixture fixture, DatabaseKind kind, int? sqlServerCompatibilityLevel = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        TestDatabase db = await fixture.CreateDatabaseAsync(kind, sqlServerCompatibilityLevel);
        db.MigrateUp();
        await db.ExecuteAsync(db.IsPostgres
            ? "CREATE TABLE test_widgets (id bigserial PRIMARY KEY, name varchar(200) NOT NULL)"
            : "CREATE TABLE test_widgets (id bigint IDENTITY PRIMARY KEY, name nvarchar(200) NOT NULL)");
        return db;
    }

    /// <summary>Creates a fresh database and one host on it.</summary>
    public static async Task<Phase4Host> StartAsync(JobsDatabaseFixture fixture, DatabaseKind kind, Action<NinjaQueueOptions>? configure = null)
        => Build(await CreateDatabaseAsync(fixture, kind), configure);

    /// <summary>Builds a host (one DI container, one potential worker) on an existing database.</summary>
    public static Phase4Host Build(TestDatabase db, Action<NinjaQueueOptions>? configure = null, Action<NinjaQueueBuilder>? extraHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        string provider = db.IsPostgres ? "Postgres" : "SqlServer";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                [$"ConnectionStrings:{provider}Write"] = db.ConnectionString,
                [$"ConnectionStrings:{provider}ReadOnly"] = db.ConnectionString,
            })
            .Build();

        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddTestHostPersistence(db);
        services.AddScoped<WidgetProvider>();
        services.AddSingleton<FaultPlan>();
        services.AddSingleton(new Phase4Probe(db));
        NinjaQueueBuilder builder = services.AddNinjaQueue(configuration, o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(2);
            o.Worker.PollInterval = TimeSpan.FromMilliseconds(50);
            o.Worker.ShutdownDrainPeriod = TimeSpan.FromSeconds(5);
            o.Worker.ShutdownCancelPeriod = TimeSpan.FromSeconds(3);
            o.Worker.ShutdownCleanupPeriod = TimeSpan.FromSeconds(3);
            o.Worker.MaintenanceInterval = TimeSpan.FromMinutes(10);
            configure?.Invoke(o);
        }).UseTestDatabase(db);
        builder
            .AddJobHandler<CreateWidgetsHandler>()
            .AddJobHandler<P4StandardHandler>()
            .AddJobHandler<P4OtherHandler>()
            .AddJobHandler<P4TimeoutHandler>()
            .AddJobHandler<RaceHandler>()
            .AddJobHandler<RaceNonInterruptibleHandler>()
            .AddJobHandler<FencedTimeoutHandler>()
            .AddJobHandler<GuardHandler>()
            .AddJobHandler<FencedCountHandler>();
        extraHandlers?.Invoke(builder);
        return new Phase4Host(db, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task InScopeAsync(Func<IServiceProvider, Task> work)
        => InScopeAsync(async sp =>
        {
            await work(sp);
            return true;
        });

    public Task<JobId> EnqueueAsync<TPayload>(TPayload payload, JobOptions? options = null)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().EnqueueAsync(payload, options));

    public Task<IReadOnlyList<JobId>> EnqueueManyAsync<TPayload>(IReadOnlyList<TPayload> payloads, JobOptions? options = null)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().EnqueueManyAsync<TPayload>([.. payloads.Select(p => (p, options))]));

    /// <summary>The real batch claim (§15.2) through the store.</summary>
    public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(IReadOnlyList<string> types, int maxJobs = 1, string queue = "default", TimeSpan? lease = null, string worker = "p4-worker")
        => InScopeAsync(sp => sp.GetRequiredService<IJobStore>().ClaimAsync(queue, types, worker, maxJobs, lease ?? TimeSpan.FromMinutes(1), CancellationToken.None));

    public async Task<ClaimedJob?> ClaimOneAsync(string type, string queue = "default", TimeSpan? lease = null)
        => (await ClaimAsync([type], 1, queue, lease)).SingleOrDefault();

    /// <summary>Runs one claimed job through the real executor (resolved from DI).</summary>
    public async Task<ExecutionResult> ExecuteAsync(ClaimedJob job, CancellationToken shutdown = default)
    {
        using JobSignals signals = new(shutdown);
        return await Get<JobExecutor>().ExecuteAsync(job, signals);
    }

    public async Task<ExecutionResult?> RunOneAsync(string type, string queue = "default")
        => await ClaimOneAsync(type, queue) is { } job ? await ExecuteAsync(job) : null;

    /// <summary>Claims and executes the job on <paramref name="queue"/> until <paramref name="id"/> is terminal.</summary>
    public async Task<JobStatus> RunUntilTerminalAsync(JobId id, string type, string queue = "default")
    {
        for (int i = 0; i < 20 && !(await StatusAsync(id)).IsTerminal(); i++)
        {
            await WaitUntilAvailableAsync(id);
            await RunOneAsync(type, queue);
        }

        return await StatusAsync(id);
    }

    public Task<int> RecoverAsync()
        => InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().RecoverExpiredLeasesAsync(100, _ => TimeSpan.Zero, CancellationToken.None));

    /// <summary>Starts this host's queue workers (the full worker path: claim loop, executor, renewer, maintenance).</summary>
    public async Task StartWorkersAsync()
    {
        await Get<NinjaQueueWorkerHost>().StartAsync(CancellationToken.None);
        _workersStarted = true;
    }

    public async Task StopWorkersAsync()
    {
        if (_workersStarted)
        {
            _workersStarted = false;
            await Get<NinjaQueueWorkerHost>().StopAsync(CancellationToken.None);
        }
    }

    // ---- verification from a second connection ---------------------------------------------------------------------

    public Task<long> DbIdAsync(JobId id) => Database.ScalarAsync<long>($"SELECT id FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'");

    public async Task<JobStatus> StatusAsync(JobId id)
        => (JobStatus)(await Database.ScalarAsync<short?>($"SELECT status FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'")
            ?? throw new InvalidOperationException($"Job {id} not found."));

    public Task<int> CountQueueAsync(JobId id)
        => Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{id.Value}'");

    public async Task<List<string>> EventsAsync(JobId id)
        => [.. (await Database.QueryAsync($"SELECT e.event_code FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id = '{id.Value}' ORDER BY e.id")).Select(r => (string)r[0]!)];

    /// <summary>(attempt, charged attempt rows, max_attempts).</summary>
    public async Task<(int Attempt, int Charged, int MaxAttempts)> AttemptsAsync(JobId id)
    {
        object?[] row = (await Database.QueryAsync($"""
            SELECT b.attempt, (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {Database.Bool(true)}), b.max_attempts
              FROM ninjaqueue.background_jobs b WHERE b.guid_id = '{id.Value}'
            """)).Single();
        return (Int(row[0]), Int(row[1]), Int(row[2]));
    }

    /// <summary>The queue row: (lease_token, attempts_left), or null when there is none.</summary>
    public async Task<(Guid? LeaseToken, int AttemptsLeft)?> QueueRowAsync(JobId id)
    {
        List<object?[]> rows = await Database.QueryAsync($"""
            SELECT q.lease_token, q.attempts_left FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{id.Value}'
            """);
        return rows.Count == 0 ? null : (rows[0][0] is null ? null : (Guid)rows[0][0]!, Int(rows[0][1]));
    }

    public Task<short?> TerminalReasonAsync(JobId id)
        => Database.ScalarAsync<short?>($"SELECT terminal_reason_code FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'");

    /// <summary>The outcomes of the job's attempt rows in order.</summary>
    public async Task<List<(int Seq, short? Outcome, bool Charged)>> AttemptRowsAsync(JobId id)
        => [.. (await Database.QueryAsync($"""
            SELECT a.attempt_seq, a.outcome, a.charged FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id
             WHERE b.guid_id = '{id.Value}' ORDER BY a.attempt_seq
            """)).Select(r => (Int(r[0]), r[1] is null ? (short?)null : Convert.ToInt16(r[1], CultureInfo.InvariantCulture), Convert.ToBoolean(r[2], CultureInfo.InvariantCulture)))];

    /// <summary>Business rows written for a job (handlers name them "{guid:N}-...").</summary>
    public Task<int> WidgetsForAsync(JobId id, string? suffix = null)
        => Database.ScalarAsync<int>($"SELECT count(*) FROM test_widgets WHERE name LIKE '{id.Value:N}-{suffix}%'");

    public Task<int> CountWidgetsAsync(string prefix) => Database.ScalarAsync<int>($"SELECT count(*) FROM test_widgets WHERE name LIKE '{prefix}%'");

    public Task<string?> CheckpointAsync(JobId id)
        => Database.ScalarAsync<string>($"SELECT {(Database.IsPostgres ? "checkpoint" : "[checkpoint]")} FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'");

    /// <summary>Waits until the job's queue row is claimable (available_at reached).</summary>
    public async Task WaitUntilAvailableAsync(JobId id, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 20)
        {
            if (await Database.ScalarAsync<int>($"""
                    SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id
                     WHERE b.guid_id = '{id.Value}' AND q.lease_token IS NULL AND q.available_at_utc <= {Database.Now}
                    """) == 1)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Job {id} did not become claimable within {timeoutMs} ms.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopWorkersAsync();
        await Services.DisposeAsync();

        // Each test database has its own connection string, so idle pooled connections would pile up on the shared
        // containers across the run (PostgreSQL max_connections); release this database's idle connections.
        await using DbConnection connection = Database.CreateConnection();
        if (connection is NpgsqlConnection npgsql)
        {
            NpgsqlConnection.ClearPool(npgsql);
        }
        else if (connection is SqlConnection sql)
        {
            SqlConnection.ClearPool(sql);
        }
    }

    internal static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
}