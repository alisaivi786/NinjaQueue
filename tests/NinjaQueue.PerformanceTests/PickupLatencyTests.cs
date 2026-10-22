namespace NinjaQueue.PerformanceTests;

/// <summary>
/// §14.8 p95 enqueue → start: &lt; poll interval + 1 s without LISTEN/NOTIFY (enqueued on another instance, so only polling
/// can pick it up), &lt; 2 s with LISTEN/NOTIFY (PostgreSQL only). The same-instance in-process signal is reported against
/// the 2 s bound. Latency is measured on the database clock: <c>started_at_utc - created_at_utc</c>.
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class PickupLatencyTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int Jobs = 150;

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Polling_pickup_p95_is_below_poll_interval_plus_one_second(DatabaseKind kind)
    {
        Percentiles latency = await MeasureAsync(kind, sameInstance: false, settings: null);
        double target = PollInterval.TotalMilliseconds + 1000;
        PerfReport.Write(output, kind, $"enqueue -> start, polling ({PollInterval.TotalSeconds:F0} s), other instance, ms", latency.ToString(), $"p95 < {target:F0} ms", latency.P95 < target);
        latency.P95.Should().BeLessThan(target, "§14.8: p95 enqueue → start < poll interval + 1 s without LISTEN/NOTIFY");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task In_process_signal_pickup_p95_is_below_two_seconds(DatabaseKind kind)
    {
        Percentiles latency = await MeasureAsync(kind, sameInstance: true, settings: null);
        PerfReport.Write(output, kind, "enqueue -> start, in-process signal, same instance, ms", latency.ToString(), "p95 < 2000 ms", latency.P95 < 2000);
        latency.P95.Should().BeLessThan(2000);
    }

    [Fact]
    public async Task Listen_notify_pickup_p95_is_below_two_seconds()
    {
        // The Release 2 listener (Phase 6b) is opt-in through NinjaQueue:ListenNotify; PostgreSQL only.
        Percentiles latency = await MeasureAsync(DatabaseKind.Postgres, sameInstance: false,
            settings: new Dictionary<string, string?> { ["NinjaQueue:ListenNotify:Enabled"] = "true" });
        PerfReport.Write(output, DatabaseKind.Postgres, "enqueue -> start, LISTEN/NOTIFY, other instance, ms", latency.ToString(), "p95 < 2000 ms", latency.P95 < 2000);
        latency.P95.Should().BeLessThan(2000, "§14.8: p95 enqueue → start < 2 s with LISTEN/NOTIFY");
    }

    private async Task<Percentiles> MeasureAsync(DatabaseKind kind, bool sameInstance, IReadOnlyDictionary<string, string?>? settings)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        void Workers(NinjaQueueOptions o)
        {
            o.Worker.Queues = new(StringComparer.Ordinal) { [LatencyHandler.QueueName] = 4 };
            o.Worker.PollInterval = PollInterval;
        }

        // Ownership moves to the finally block below, which disposes every host.
#pragma warning disable CA2000
        List<PerfHost> workers = [PerfHost.Build(db, "perf-lat-w0", Workers, settings), PerfHost.Build(db, "perf-lat-w1", Workers, settings)];
        PerfHost enqueuer = sameInstance ? workers[0] : PerfHost.Build(db, "perf-lat-api", o => o.Worker.Enabled = false, settings);
#pragma warning restore CA2000
        try
        {
            await Task.WhenAll(workers.Select(w => w.StartWorkersAsync()));
            await Task.Delay(PollInterval * 2);
            for (int i = 0; i < Jobs; i++)
            {
                await enqueuer.EnqueueAsync(new LatencyPayload(i));
                await Task.Delay(System.Security.Cryptography.RandomNumberGenerator.GetInt32(50, 400));
            }

            try
            {
                await Wait.UntilAsync(async () => await db.PendingAsync() == 0, TimeSpan.FromMinutes(2));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Jobs did not finish. {await DiagnoseAsync(db)}", ex);
            }
            List<double> samples = await db.DoublesAsync($"SELECT {db.MsBetween("created_at_utc", "started_at_utc")} FROM ninjaqueue.background_jobs WHERE job_type = '{LatencyHandler.Code}'");
            samples.Should().HaveCount(Jobs);
            return new Percentiles(samples);
        }
        finally
        {
            if (!sameInstance)
            {
                await enqueuer.DisposeAsync();
            }

            await PerfHost.DisposeAllAsync(workers);
            db.ClearPools();
        }
    }

    private static async Task<string> DiagnoseAsync(PerfDatabase db)
    {
        IEnumerable<string> statuses = (await db.QueryAsync("SELECT status, count(*) FROM ninjaqueue.background_jobs GROUP BY status")).Select(r => $"status {r[0]}: {r[1]}");
        IEnumerable<string> queue = (await db.QueryAsync($"""
            SELECT queue, CASE WHEN lease_token IS NULL THEN 'unleased' ELSE 'leased' END,
                   CASE WHEN available_at_utc <= {db.Now} THEN 'ready' ELSE 'future' END, count(*)
              FROM ninjaqueue.job_queue GROUP BY queue, CASE WHEN lease_token IS NULL THEN 'unleased' ELSE 'leased' END,
                   CASE WHEN available_at_utc <= {db.Now} THEN 'ready' ELSE 'future' END
            """)).Select(r => $"{r[0]}/{r[1]}/{r[2]}: {r[3]}");
        IEnumerable<string> workers = (await db.QueryAsync("SELECT worker_id, queues FROM ninjaqueue.job_workers")).Select(r => $"{r[0]} [{r[1]}]");
        return $"Jobs: {string.Join(", ", statuses)}. Queue rows: {string.Join(", ", queue)}. Workers: {string.Join("; ", workers)}.";
    }
}