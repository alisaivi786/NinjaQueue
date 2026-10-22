using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.Execution;
using NinjaQueue.Hosting;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// A real <see cref="IHost"/> wired like an application (plain EF Core persistence, <c>AddNinjaQueue().UseEfCore()</c>),
/// over an existing database, so several hosts can share one database (rolling deploy, two maintenance leaders) and the
/// connection string can be changed (a restricted login, an unreachable server, a dedicated pausable container). The host is
/// started and stopped only when a test asks for it (workers, heartbeat and maintenance run inside <see cref="StartAsync"/>).
/// </summary>
internal sealed class WorkerTestHost : IAsyncDisposable
{
    private bool _started;
    private bool _stopped;

    private WorkerTestHost(TestDatabase database, IHost host)
    {
        Database = database;
        Host = host;
    }

    public TestDatabase Database { get; }

    public IHost Host { get; }

    /// <summary>Warnings and errors the host logged (the engine logs a failed outcome write or claim here), for diagnostics.</summary>
    public string Logs => string.Join(" || ", Services.GetServices<ILoggerProvider>().OfType<LogSink>().SelectMany(s => s.Entries));

    public IServiceProvider Services => Host.Services;

    /// <summary>Creates a database of <paramref name="kind"/>, migrates it and adds the business table.</summary>
    public static async Task<TestDatabase> CreateDatabaseAsync(JobsDatabaseFixture fixture, DatabaseKind kind)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        TestDatabase db = await fixture.CreateDatabaseAsync(kind);
        await PrepareAsync(db);
        return db;
    }

    /// <summary>Migrates <paramref name="db"/> and adds the business table.</summary>
    public static async Task PrepareAsync(TestDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.MigrateUp();
        await db.ExecuteAsync(db.IsPostgres
            ? "CREATE TABLE test_widgets (id bigserial PRIMARY KEY, name varchar(200) NOT NULL)"
            : "CREATE TABLE test_widgets (id bigint IDENTITY PRIMARY KEY, name nvarchar(200) NOT NULL)");
    }

    /// <summary>Builds (does not start) a host over <paramref name="db"/>; <paramref name="connectionString"/> overrides the engine's.</summary>
    public static WorkerTestHost Create(
        TestDatabase db, Action<NinjaQueueBuilder> handlers, Action<NinjaQueueOptions>? configure = null, string? connectionString = null,
        Action<IServiceCollection>? services = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(handlers);
        string provider = db.IsPostgres ? "Postgres" : "SqlServer";
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
            [$"ConnectionStrings:{provider}Write"] = connectionString ?? db.ConnectionString,
            [$"ConnectionStrings:{provider}ReadOnly"] = connectionString ?? db.ConnectionString,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<ILoggerProvider, LogSink>();
        builder.Services.AddTestHostPersistence(db, connectionString);
        builder.Services.AddScoped<WidgetProvider>();
        builder.Services.AddSingleton<FaultPlan>();
        builder.Services.AddSingleton<ProbePlan>();
        builder.Services.AddSingleton<FencedProbePlan>();
        NinjaQueueBuilder jobs = builder.Services.AddNinjaQueue(builder.Configuration, o =>
        {
            o.Retry.BaseDelay = TimeSpan.FromMilliseconds(1);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(2);
            o.Worker.PollInterval = TimeSpan.FromMilliseconds(200);
            configure?.Invoke(o);
        }).UseTestDatabase(db);
        handlers(jobs);
        services?.Invoke(builder.Services);
        return new WorkerTestHost(db, builder.Build());
    }

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    public NinjaQueueWorkerHost Worker => Get<NinjaQueueWorkerHost>();

    /// <summary>Starts the host: registry validation, startup checks, then workers, heartbeat and maintenance.</summary>
    public async Task StartAsync()
    {
        await Host.StartAsync();
        _started = true;
    }

    /// <summary>Stops the host (the §16.2 shutdown) and returns how long it took.</summary>
    public async Task<TimeSpan> StopAsync()
    {
        long started = Stopwatch.GetTimestamp();
        await Host.StopAsync();
        _stopped = true;
        return Stopwatch.GetElapsedTime(started);
    }

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

    /// <summary>Claims one job of <paramref name="jobType"/> with <paramref name="lease"/>.</summary>
    public async Task<ClaimedJob?> ClaimOneAsync(string jobType, TimeSpan? lease = null, string queue = "default")
        => (await InScopeAsync(sp => sp.GetRequiredService<IJobStore>()
            .ClaimAsync(queue, [jobType], "test-worker", 1, lease ?? TimeSpan.FromMinutes(1), CancellationToken.None))).SingleOrDefault();

    /// <summary>Claims and executes one job with the real executor.</summary>
    public async Task<ExecutionResult?> RunOneAsync(string jobType, CancellationToken shutdown = default)
    {
        ClaimedJob? job = await ClaimOneAsync(jobType);
        if (job is null)
        {
            return null;
        }

        using JobSignals signals = new(shutdown);
        return await Get<JobExecutor>().ExecuteAsync(job, signals);
    }

    /// <summary>One expired-lease recovery round (no backoff).</summary>
    public Task<int> RecoverAsync()
        => InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().RecoverExpiredLeasesAsync(100, _ => TimeSpan.Zero, CancellationToken.None));

    /// <summary>One watchdog round with <paramref name="grace"/>.</summary>
    public Task<int> KillOverdueAsync(TimeSpan grace)
        => InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().KillOverdueFencedStepsAsync(grace, CancellationToken.None));

    /// <summary>
    /// Drives a job to a terminal state the way production would: recovers it once its lease expires, and runs it whenever it is
    /// claimable. Returns false if it is not terminal within <paramref name="timeout"/>.
    /// </summary>
    public Task<bool> DriveToTerminalAsync(JobId id, string jobType, TimeSpan timeout)
        => JobProbes.EventuallyAsync(async () =>
        {
            JobStatus? status = await Database.StatusOfAsync(id);
            if (status is { } s && s.IsTerminal())
            {
                return true;
            }

            if (status == JobStatus.Processing && (await Database.QueueStateAsync(id))?.Expired == true)
            {
                await RecoverAsync();
            }
            else if (status is JobStatus.Enqueued or JobStatus.Scheduled)
            {
                await Database.MakeAvailableNowAsync(id);
                await RunOneAsync(jobType);
            }

            return false;
        }, timeout, TimeSpan.FromMilliseconds(500));

    public Task<HealthCheckResult> HealthAsync(CancellationToken cancellationToken = default)
    {
        NinjaQueueHealthCheck check = Get<NinjaQueueHealthCheck>();
        return check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("ninjaqueue", check, null, null) }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && !_stopped)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(40));
                await Host.StopAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or DbException)
            {
                // Best effort in teardown.
            }
        }

        if (Host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            Host.Dispose();
        }
    }
}

/// <summary>Verification from a second connection, shared by <see cref="JobsHost"/> and <see cref="WorkerTestHost"/> tests.</summary>
internal static class JobProbes
{
    private static string Guid(JobId id) => id.Value.ToString();

    public static async Task<JobStatus?> StatusOfAsync(this TestDatabase db, JobId id)
        => await db.ScalarAsync<short?>($"SELECT status FROM ninjaqueue.background_jobs WHERE guid_id = '{Guid(id)}'") is { } s ? (JobStatus)s : null;

    public static Task<int> QueueRowsAsync(this TestDatabase db, JobId id)
        => db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{Guid(id)}'");

    public static async Task<List<string>> EventsOfAsync(this TestDatabase db, JobId id)
        => [.. (await db.QueryAsync($"SELECT e.event_code FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id = '{Guid(id)}' ORDER BY e.id")).Select(r => (string)r[0]!)];

    /// <summary>(attempt, charged attempt rows, max_attempts).</summary>
    public static async Task<(int Attempt, int Charged, int MaxAttempts)> AttemptsOfAsync(this TestDatabase db, JobId id)
    {
        object?[] row = (await db.QueryAsync($"""
            SELECT b.attempt, (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {db.Bool(true)}), b.max_attempts
              FROM ninjaqueue.background_jobs b WHERE b.guid_id = '{Guid(id)}'
            """)).Single();
        return (Int(row[0]), Int(row[1]), Int(row[2]));
    }

    /// <summary>One column of the job row.</summary>
    public static Task<T?> JobColumnAsync<T>(this TestDatabase db, JobId id, string column)
        => db.ScalarAsync<T>($"SELECT {column} FROM ninjaqueue.background_jobs WHERE guid_id = '{Guid(id)}'");

    /// <summary>True when the job row's <paramref name="column"/> is NULL.</summary>
    public static async Task<bool> IsNullAsync(this TestDatabase db, JobId id, string column)
        => await db.ScalarAsync<int>($"SELECT CASE WHEN {column} IS NULL THEN 1 ELSE 0 END FROM ninjaqueue.background_jobs WHERE guid_id = '{Guid(id)}'") == 1;

    /// <summary>(lease_token is set, lease_expires_at_utc is past, attempts_left) of the queue row, or null without one.</summary>
    public static async Task<(bool Leased, bool Expired, int AttemptsLeft)?> QueueStateAsync(this TestDatabase db, JobId id)
    {
        List<object?[]> rows = await db.QueryAsync($"""
            SELECT CASE WHEN q.lease_token IS NULL THEN 0 ELSE 1 END,
                   CASE WHEN q.lease_expires_at_utc < {db.Now} THEN 1 ELSE 0 END, q.attempts_left
              FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{Guid(id)}'
            """);
        return rows.Count == 0 ? null : (Int(rows[0][0]) == 1, Int(rows[0][1]) == 1, Int(rows[0][2]));
    }

    /// <summary>Outcomes of the job's attempt rows in order (null while open).</summary>
    /// <summary>SQL Server blocking chain (waiter → blocker and the blocker's last statement), for assertion messages.</summary>
    public static async Task<string> BlockingAsync(this TestDatabase db)
    {
        if (db.IsPostgres)
        {
            return string.Join(" | ", (await db.QueryAsync("""
                SELECT a.pid, pg_blocking_pids(a.pid)::text, left(a.query, 300), b.state, left(b.query, 300)
                  FROM pg_stat_activity a JOIN pg_stat_activity b ON b.pid = ANY(pg_blocking_pids(a.pid))
                """)).Select(r => string.Join(" / ", r)));
        }

        return string.Join(" | ", (await db.QueryAsync("""
            SELECT r.session_id, r.blocking_session_id, r.wait_type, LEFT(wt.text, 300), s.status, LEFT(bt.text, 400)
              FROM sys.dm_exec_requests r
              CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) wt
              JOIN sys.dm_exec_sessions s ON s.session_id = r.blocking_session_id
              JOIN sys.dm_exec_connections c ON c.session_id = r.blocking_session_id
              CROSS APPLY sys.dm_exec_sql_text(c.most_recent_sql_handle) bt
             WHERE r.blocking_session_id <> 0
            """)).Select(r => string.Join(" / ", r)));
    }

    /// <summary>"outcome:error_code:error" per attempt, for assertion messages.</summary>
    public static async Task<string> AttemptTrailAsync(this TestDatabase db, JobId id)
        => string.Join(" | ", (await db.QueryAsync($"SELECT a.outcome, a.error_code, a.error FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE b.guid_id = '{Guid(id)}' ORDER BY a.attempt_seq"))
            .Select(r => $"{r[0]}:{r[1]}:{r[2]}"));

    public static async Task<List<JobAttemptOutcome?>> AttemptOutcomesAsync(this TestDatabase db, JobId id)
        => [.. (await db.QueryAsync($"SELECT a.outcome FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE b.guid_id = '{Guid(id)}' ORDER BY a.attempt_seq"))
            .Select(r => r[0] is null ? (JobAttemptOutcome?)null : (JobAttemptOutcome)Convert.ToInt16(r[0], System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Makes the job's queue row available now (skips backoff and snooze delays).</summary>
    public static Task<int> MakeAvailableNowAsync(this TestDatabase db, JobId id)
        => db.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET available_at_utc = {db.Now} WHERE job_id = (SELECT id FROM ninjaqueue.background_jobs WHERE guid_id = '{Guid(id)}')");

    public static Task<int> WidgetCountAsync(this TestDatabase db, string? prefix = null)
        => db.ScalarAsync<int>($"SELECT count(*) FROM test_widgets{(db.Kind == DatabaseKind.SqlServerRcsiOff ? " WITH (READPAST)" : "")}{(prefix is null ? "" : $" WHERE name LIKE '{prefix}%'")}");

    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan? every = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            if (await condition())
            {
                return true;
            }

            if (Stopwatch.GetElapsedTime(started) > timeout)
            {
                return false;
            }

            await Task.Delay(every ?? TimeSpan.FromMilliseconds(100));
        }
    }

    private static int Int(object? value) => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Helpers on <see cref="JobsHost"/> for the Phase 5 suites (additive; JobsHost itself is unchanged).</summary>
internal static class JobsHostExtensions
{
    /// <summary>Claims one job with a chosen lease (the real §15.2 claim).</summary>
    public static async Task<ClaimedJob?> ClaimWithLeaseAsync(this JobsHost host, string jobType, TimeSpan lease, string workerId = "test-worker")
    {
        ArgumentNullException.ThrowIfNull(host);
        IReadOnlyList<ClaimedJob> claimed = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>()
            .ClaimAsync("default", [jobType], workerId, 1, lease, CancellationToken.None));
        return claimed.SingleOrDefault();
    }

    /// <summary>Executes a claimed job with signals linked to <paramref name="shutdown"/>.</summary>
    public static async Task<ExecutionResult> ExecuteAsync(this JobsHost host, ClaimedJob job, CancellationToken shutdown = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        using JobSignals signals = new(shutdown);
        return await host.Get<JobExecutor>().ExecuteAsync(job, signals);
    }

    /// <summary>Runs one expired-lease recovery round (no backoff) and returns the number recovered.</summary>
    public static Task<int> RecoverAsync(this JobsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().RecoverExpiredLeasesAsync(100, _ => TimeSpan.Zero, CancellationToken.None));
    }

    /// <summary>Expires the job's lease now (instead of waiting for it).</summary>
    public static Task<int> ExpireLeaseAsync(this TestDatabase db, long jobId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ExecuteAsync(db.IsPostgres
            ? $"UPDATE ninjaqueue.job_queue SET lease_expires_at_utc = now() - interval '1 second' WHERE job_id = {jobId}"
            : $"UPDATE ninjaqueue.job_queue SET lease_expires_at_utc = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE job_id = {jobId}");
    }
}

/// <summary>Keeps the host's warning/error log lines in memory so a failing test can print them.</summary>
internal sealed class LogSink : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();

    public IEnumerable<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Sink(LogSink owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel) && owner._entries.Count < 200)
            {
                owner._entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
            }
        }
    }
}