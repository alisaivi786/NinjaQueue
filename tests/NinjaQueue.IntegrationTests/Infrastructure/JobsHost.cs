using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// The engine wired exactly as an application wires it: plain EF Core persistence over
/// <see cref="TestHostDbContext"/>, <c>AddNinjaQueue().UseEfCore()</c> and the test handlers, on a migrated database with the
/// business table created.
/// </summary>
public sealed class JobsHost : IAsyncDisposable
{
    private JobsHost(TestDatabase database, ServiceProvider services)
    {
        Database = database;
        Services = services;
    }

    public TestDatabase Database { get; }

    public ServiceProvider Services { get; }

    public static async Task<JobsHost> StartAsync(JobsDatabaseFixture fixture, DatabaseKind kind, Action<NinjaQueueBuilder> handlers, Action<NinjaQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(handlers);
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);
        db.MigrateUp();
        await db.ExecuteAsync(db.IsPostgres
            ? "CREATE TABLE test_widgets (id bigserial PRIMARY KEY, name varchar(200) NOT NULL)"
            : "CREATE TABLE test_widgets (id bigint IDENTITY PRIMARY KEY, name nvarchar(200) NOT NULL)");

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
        NinjaQueueBuilder builder = services.AddNinjaQueue(configuration, o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(2);
            configure?.Invoke(o);
        }).UseTestDatabase(db);
        handlers(builder);
        return new JobsHost(db, services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    /// <summary>Runs <paramref name="work"/> in a new DI scope.</summary>
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

    /// <summary>Stages a job inside a transactional unit of work, optionally with a business write and a failure before commit.</summary>
    public Task<JobId> StageInTransactionAsync<TPayload>(TPayload payload, JobOptions? options = null, string? widget = null, bool failBeforeCommit = false)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>().UseTransactionAsync(async (session, ct) =>
        {
            if (widget is not null)
            {
                session.DbContext.Widgets.Add(new Widget { Name = widget });
                await session.DbContext.SaveChangesAsync(ct);
            }

            JobId id = await sp.GetRequiredService<IJobEnqueuer>().StageAsync(payload, options, ct);
            return failBeforeCommit ? throw new InvalidOperationException("Injected failure before commit.") : id;
        }));

    public Task<JobId> EnqueueAsync<TPayload>(TPayload payload, JobOptions? options = null)
        where TPayload : notnull
        => InScopeAsync(sp => sp.GetRequiredService<IJobEnqueuer>().EnqueueAsync(payload, options));

    /// <summary>Claims one job of <paramref name="jobType"/> on <paramref name="queue"/> (the real §15.2 claim).</summary>
    public async Task<ClaimedJob?> ClaimOneAsync(string jobType, string queue = "default")
    {
        IReadOnlyList<ClaimedJob> claimed = await InScopeAsync(sp => sp.GetRequiredService<IJobStore>()
            .ClaimAsync(queue, [jobType], "test-worker", 1, TimeSpan.FromMinutes(1), CancellationToken.None));
        return claimed.SingleOrDefault();
    }

    /// <summary>Claims and executes one job with the real executor.</summary>
    public async Task<ExecutionResult?> RunOneAsync(string jobType, string queue = "default")
    {
        ClaimedJob? job = await ClaimOneAsync(jobType, queue);
        if (job is null)
        {
            return null;
        }

        using JobSignals signals = new(CancellationToken.None);
        return await Get<JobExecutor>().ExecuteAsync(job, signals);
    }

    // ---- verification from a second connection ---------------------------------------------------------------------

    public Task<int> CountWidgetsAsync(string? prefix = null)
        => Database.ScalarAsync<int>($"SELECT count(*) FROM test_widgets{(Database.Kind == DatabaseKind.SqlServerRcsiOff ? " WITH (READPAST)" : "")}{(prefix is null ? "" : $" WHERE name LIKE '{prefix}%'")}");

    public async Task<JobStatus?> StatusAsync(JobId id)
        => await Database.ScalarAsync<short?>($"SELECT status FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'") is { } s ? (JobStatus)s : null;

    public Task<int> CountJobsAsync() => Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs");

    public Task<int> CountQueueAsync(JobId id)
        => Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{id.Value}'");

    public async Task<List<string>> EventsAsync(JobId id)
        => [.. (await Database.QueryAsync($"SELECT e.event_code FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id = '{id.Value}' ORDER BY e.id")).Select(r => (string)r[0]!)];

    public async Task<(int Attempt, int Charged, int MaxAttempts)> AttemptsAsync(JobId id)
    {
        object?[] row = (await Database.QueryAsync($"""
            SELECT b.attempt, (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {Database.Bool(true)}), b.max_attempts
              FROM ninjaqueue.background_jobs b WHERE b.guid_id = '{id.Value}'
            """)).Single();
        return (Convert.ToInt32(row[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(row[1], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(row[2], System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();
}

/// <summary>A provider that wraps its writes in its own unit of work, like every HCM provider: alone it commits by itself.</summary>
public sealed class WidgetProvider(AppUnitOfWork unitOfWork)
{
    public Task AddAsync(string name, CancellationToken ct)
        => unitOfWork.UseAsync((session, _) =>
        {
            session.DbContext.Widgets.Add(new Widget { Name = name });
            return Task.CompletedTask;
        }, ct);
}

/// <summary>Where a test handler should fail or misbehave.</summary>
public sealed class FaultPlan
{
    public string? ThrowAfterWriteAtCheckpoint { get; set; }

    public bool CommitEngineTransaction { get; set; }

    public bool OpenSecondUnitOfWork { get; set; }

    public bool ThrowPermanent { get; set; }

    public Func<Task>? DuringStep { get; set; }

    public int Calls;
}