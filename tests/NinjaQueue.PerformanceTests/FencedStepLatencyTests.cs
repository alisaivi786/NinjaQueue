namespace NinjaQueue.PerformanceTests;

/// <summary>
/// Fenced step latency and fenced throughput through the real worker path (claim → fence → step bound → watchdog
/// registration → handler write through a nested unit of work → finalization → commit). §14.8 sets no fenced target, so the
/// numbers are reported; the test asserts the fenced invariant (exactly one business row per job, §14.8 invariant 4).
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class FencedStepLatencyTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Fenced_step_latency_and_throughput_with_exactly_one_business_row_per_job(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        List<PerfHost> hosts = [];
        try
        {
            // 1) Latency: one instance, one slot, so nothing queues behind anything.
            const int Sequential = 500;
            PerfHost api = PerfHost.Build(db, "perf-fenced-api", o => o.Worker.Enabled = false);
            hosts.Add(api);
            await api.EnqueueManyAsync([.. Enumerable.Range(0, Sequential).Select(i => new FencedPayload(i))]);
            PerfHost single = PerfHost.Build(db, "perf-fenced-w0", o => o.Worker.Queues = new(StringComparer.Ordinal) { [FencedHandler.QueueName] = 1 });
            hosts.Add(single);
            await single.StartWorkersAsync();
            await Wait.UntilAsync(async () => await db.PendingAsync() == 0, TimeSpan.FromMinutes(5), 200);
            await single.StopWorkersAsync();
            Percentiles latency = new(await db.DoublesAsync($"SELECT {db.MsBetween("started_at_utc", "completed_at_utc")} FROM ninjaqueue.background_jobs WHERE status = 10"));
            PerfReport.Write(output, kind, "fenced job claim-to-commit latency (1 slot), ms", latency.ToString(), "none in §14.8 (reported)", true);

            // 2) Throughput: four instances x 16 slots.
            int jobs = Math.Max(2_000, PerfScale.Jobs / 4);
            await db.ExecuteAsync("DELETE FROM perf_widgets");
            long firstId = await db.ScalarAsync<long>("SELECT COALESCE(MAX(id), 0) FROM ninjaqueue.background_jobs");
            await api.EnqueueManyAsync([.. Enumerable.Range(0, jobs).Select(i => new FencedPayload(i))]);
            List<PerfHost> workers = [.. Enumerable.Range(1, 4).Select(i => PerfHost.Build(db, $"perf-fenced-w{i}", o => o.Worker.Queues = new(StringComparer.Ordinal) { [FencedHandler.QueueName] = 16 }))];
            hosts.AddRange(workers);
            await Task.WhenAll(workers.Select(w => w.StartWorkersAsync()));
            await Wait.UntilAsync(async () => await db.PendingAsync() == 0, TimeSpan.FromMinutes(10), 250);
            double windowMs = await db.ScalarAsync<double>($"SELECT {db.MsBetween("MIN(started_at_utc)", "MAX(completed_at_utc)")} FROM ninjaqueue.background_jobs WHERE id > {firstId}");
            double rate = jobs / (windowMs / 1000);
            PerfReport.Write(output, kind, "fenced throughput, 4 instances x 16", $"{rate:F0} jobs/s ({jobs} jobs)", "none in §14.8 (reported)", true);

            // Exactly one business row per fenced job, and every job succeeded.
            (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE id > {firstId} AND status = 10")).Should().Be(jobs);
            (await db.ScalarAsync<int>("SELECT count(*) FROM perf_widgets")).Should().Be(jobs);
            (await db.ScalarAsync<int>("SELECT count(*) FROM (SELECT name FROM perf_widgets GROUP BY name HAVING count(*) > 1) d")).Should().Be(0);
            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_fenced_steps")).Should().Be(0, "every watchdog registration is removed after its step");
        }
        finally
        {
            await PerfHost.DisposeAllAsync(hosts);
            db.ClearPools();
        }
    }
}