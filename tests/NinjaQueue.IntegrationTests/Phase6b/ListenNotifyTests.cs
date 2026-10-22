using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;
using static NinjaQueue.IntegrationTests.Phase6b.Phase6bSupport;

namespace NinjaQueue.IntegrationTests.Phase6b;

/// <summary>
/// Phase 6b wake-up (§14.8 target: p95 enqueue → start &lt; 2 s with LISTEN/NOTIFY, &lt; poll interval + 1 s without). Jobs are
/// enqueued from a second host that runs no workers, so the in-process signal cannot wake the worker host: only NOTIFY (or a
/// poll) can. The worker host polls every 30 s in the NOTIFY tests, so a fast start proves the notification path.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "6b")]
public sealed class ListenNotifyTests(JobsDatabaseFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(30);

    private sealed class StartLog
    {
        public ConcurrentDictionary<string, long> Started { get; } = new(StringComparer.Ordinal);
    }

    private static WorkerTestHost Worker(TestDatabase db, bool listen, TimeSpan poll, StartLog log)
    {
        WorkerTestHost host = Host(db, configure: o =>
        {
            o.ListenNotify.Enabled = listen;
            o.ListenNotify.InitialReconnectDelay = TimeSpan.FromMilliseconds(200);
            o.ListenNotify.KeepAliveInterval = TimeSpan.FromSeconds(2);
            o.Worker.PollInterval = poll;
        });
        host.Get<ProbePlan>().Behaviour = (p, _, _) =>
        {
            log.Started.TryAdd(p.Name, Stopwatch.GetTimestamp());
            return Task.FromResult(JobResult.Success());
        };
        return host;
    }

    private static WorkerTestHost Producer(TestDatabase db, bool listen)
        => Host(db, configure: o =>
        {
            o.ListenNotify.Enabled = listen;
            o.Worker.Enabled = false;
        });

    private static PostgresNotifyListener<TestHostDbContext> Listener(WorkerTestHost host) => host.Get<PostgresNotifyListener<TestHostDbContext>>();

    /// <summary>Enqueues <paramref name="count"/> jobs one at a time, each after the previous one started, and returns the
    /// enqueue-committed → handler-started latencies in milliseconds.</summary>
    private static async Task<List<double>> MeasureAsync(WorkerTestHost producer, StartLog log, string prefix, int count, TimeSpan timeout, Func<WorkerTestHost, ProbePayload, Task>? enqueue = null)
    {
        List<double> latencies = [];
        for (int i = 0; i < count; i++)
        {
            string name = $"{prefix}-{i}";
            if (enqueue is null)
            {
                await producer.EnqueueAsync(new ProbePayload(name));
            }
            else
            {
                await enqueue(producer, new ProbePayload(name));
            }

            long committed = Stopwatch.GetTimestamp();
            (await JobProbes.EventuallyAsync(() => Task.FromResult(log.Started.ContainsKey(name)), timeout, TimeSpan.FromMilliseconds(5)))
                .Should().BeTrue($"job {name} must start within {timeout}");
            latencies.Add(Math.Max(0, Stopwatch.GetElapsedTime(committed, log.Started[name]).TotalMilliseconds));
        }

        return latencies;
    }

    private void Report(string label, List<double> latencies)
        => output.WriteLine($"{label}: n={latencies.Count} p50={Percentile(latencies, 50):F1} ms p95={Percentile(latencies, 95):F1} ms max={latencies.Max():F1} ms");

    [Fact]
    public async Task Notify_wakes_an_idle_worker_in_another_instance_well_within_the_target()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        StartLog log = new();
        await using WorkerTestHost worker = Worker(db, listen: true, LongPoll, log);
        await using WorkerTestHost producer = Producer(db, listen: true);
        await worker.StartAsync();
        (await JobProbes.EventuallyAsync(() => Task.FromResult(Listener(worker).IsListening), TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Warm-up (pools, JIT), then the measured run.
        await MeasureAsync(producer, log, "warm", 3, TimeSpan.FromSeconds(5));
        List<double> enqueue = await MeasureAsync(producer, log, "enqueue", 50, TimeSpan.FromSeconds(5));
        Report("NOTIFY EnqueueAsync", enqueue);

        // Stage in the caller's transaction: the NOTIFY is delivered on commit.
        List<double> staged = await MeasureAsync(producer, log, "staged", 20, TimeSpan.FromSeconds(5), (host, payload) => host.InScopeAsync(sp =>
            sp.GetRequiredService<AppUnitOfWork>().UseTransactionAsync(System.Data.IsolationLevel.ReadCommitted, async (_, ct) =>
            {
                await sp.GetRequiredService<IJobEnqueuer>().StageAsync(payload, cancellationToken: ct);
                return 0;
            }, CancellationToken.None)));
        Report("NOTIFY StageAsync", staged);

        Percentile(enqueue, 95).Should().BeLessThan(2000, "§14.8: p95 enqueue → start < 2 s with LISTEN/NOTIFY");
        Percentile(staged, 95).Should().BeLessThan(2000);
        Percentile(enqueue, 95).Should().BeLessThan(LongPoll.TotalMilliseconds / 10, "the start came from the notification, not from a 30 s poll");
        Listener(worker).Notifications.Should().BeGreaterThanOrEqualTo(70);
    }

    [Fact]
    public async Task Rolled_back_stage_sends_no_notification()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        StartLog log = new();
        await using WorkerTestHost worker = Worker(db, listen: true, LongPoll, log);
        await using WorkerTestHost producer = Producer(db, listen: true);
        await worker.StartAsync();
        (await JobProbes.EventuallyAsync(() => Task.FromResult(Listener(worker).IsListening), TimeSpan.FromSeconds(10))).Should().BeTrue();
        int before = Listener(worker).Notifications;

        Func<Task> rolledBack = () => producer.InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>()
            .UseTransactionAsync<int>(async (_, ct) =>
            {
                await sp.GetRequiredService<IJobEnqueuer>().StageAsync(new ProbePayload("rolled-back"), cancellationToken: ct);
                throw new InvalidOperationException("Injected failure before commit.");
            }));
        await rolledBack.Should().ThrowAsync<InvalidOperationException>();

        await Task.Delay(1000);
        Listener(worker).Notifications.Should().Be(before, "NOTIFY is transactional: a rolled-back stage notifies nobody");
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(0);
    }

    [Fact]
    public async Task Listener_reconnects_after_its_connection_is_killed_and_catches_up()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        StartLog log = new();
        await using WorkerTestHost worker = Worker(db, listen: true, LongPoll, log);
        await using WorkerTestHost producer = Producer(db, listen: true);
        await worker.StartAsync();
        PostgresNotifyListener<TestHostDbContext> listener = Listener(worker);
        (await JobProbes.EventuallyAsync(() => Task.FromResult(listener.IsListening), TimeSpan.FromSeconds(10))).Should().BeTrue();
        await MeasureAsync(producer, log, "warm", 2, TimeSpan.FromSeconds(5));

        int killed = listener.BackendProcessId;
        (await db.ScalarAsync<bool>($"SELECT pg_terminate_backend({killed})")).Should().BeTrue();

        // A job enqueued while the listener is down is picked up by the catch-up signal on reconnect, not by the 30 s poll.
        long enqueuedWhileDown = Stopwatch.GetTimestamp();
        await producer.EnqueueAsync(new ProbePayload("while-down"));
        (await JobProbes.EventuallyAsync(() => Task.FromResult(listener.Connections >= 2 && listener.IsListening), TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the listener reconnects with backoff");
        listener.BackendProcessId.Should().NotBe(killed);
        (await JobProbes.EventuallyAsync(() => Task.FromResult(log.Started.ContainsKey("while-down")), TimeSpan.FromSeconds(5))).Should().BeTrue();
        output.WriteLine($"Job enqueued during the outage started after {Stopwatch.GetElapsedTime(enqueuedWhileDown, log.Started["while-down"]).TotalMilliseconds:F0} ms");

        List<double> after = await MeasureAsync(producer, log, "after", 20, TimeSpan.FromSeconds(5));
        Report("NOTIFY after reconnect", after);
        Percentile(after, 95).Should().BeLessThan(2000);
    }

    public static TheoryData<DatabaseKind, bool> FallbackCases => new()
    {
        { DatabaseKind.Postgres, false },
        { DatabaseKind.SqlServerRcsiOn, true },
        { DatabaseKind.SqlServerRcsiOff, true },
    };

    [Theory]
    [MemberData(nameof(FallbackCases))]
    public async Task Without_notify_workers_fall_back_to_polling_within_the_poll_interval(DatabaseKind kind, bool listenRequested)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        StartLog log = new();
        TimeSpan poll = TimeSpan.FromSeconds(1);
        await using WorkerTestHost worker = Worker(db, listenRequested, poll, log);
        await using WorkerTestHost producer = Producer(db, listenRequested);
        await worker.StartAsync();
        await Task.Delay(500);
        if (kind == DatabaseKind.Postgres)
        {
            Listener(worker).IsListening.Should().BeFalse("LISTEN/NOTIFY is opt-in");
        }
        else
        {
            worker.Services.GetService(typeof(PostgresNotifyListener<TestHostDbContext>)).Should().BeNull("SQL Server has no LISTEN; the option is ignored");
        }

        List<double> latencies = await MeasureAsync(producer, log, "poll", 15, TimeSpan.FromSeconds(5));
        Report($"Polling {kind} (poll {poll.TotalMilliseconds} ms)", latencies);
        Percentile(latencies, 95).Should().BeLessThan(poll.TotalMilliseconds + 1000, "§14.8: < poll interval + 1 s without LISTEN/NOTIFY");
        (await JobProbes.EventuallyAsync(async () => await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status <> 10") == 0, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("every job completes");
    }

    [Fact]
    public async Task Listener_down_still_runs_jobs_by_polling()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        StartLog log = new();
        await using WorkerTestHost worker = Host(db, configure: o =>
        {
            o.ListenNotify.Enabled = true;
            o.ListenNotify.InitialReconnectDelay = TimeSpan.FromSeconds(30);
            o.ListenNotify.MaxReconnectDelay = TimeSpan.FromSeconds(30);
            o.Worker.PollInterval = TimeSpan.FromSeconds(1);
        });
        worker.Get<ProbePlan>().Behaviour = (p, _, _) =>
        {
            log.Started.TryAdd(p.Name, Stopwatch.GetTimestamp());
            return Task.FromResult(JobResult.Success());
        };
        await using WorkerTestHost producer = Producer(db, listen: true);
        await worker.StartAsync();
        PostgresNotifyListener<TestHostDbContext> listener = Listener(worker);
        (await JobProbes.EventuallyAsync(() => Task.FromResult(listener.IsListening), TimeSpan.FromSeconds(10))).Should().BeTrue();
        await db.ScalarAsync<bool>($"SELECT pg_terminate_backend({listener.BackendProcessId})");
        (await JobProbes.EventuallyAsync(() => Task.FromResult(!listener.IsListening), TimeSpan.FromSeconds(5))).Should().BeTrue();

        List<double> latencies = await MeasureAsync(producer, log, "down", 5, TimeSpan.FromSeconds(5));
        Report("Listener down, polling 1 s", latencies);
        listener.IsListening.Should().BeFalse("the reconnect is 30 s away: these jobs ran by polling");
        Percentile(latencies, 95).Should().BeLessThan(2000);
    }
}