namespace NinjaQueue.PerformanceTests;

/// <summary>§14.8 bulk enqueue: <c>EnqueueManyAsync</c> ≥ 5 000 jobs/s. Single-job and parallel-caller rates are reported.</summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class EnqueueThroughputTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private const double TargetJobsPerSecond = 5_000;

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Bulk_enqueue_reaches_5000_jobs_per_second(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        PerfHost host = PerfHost.Build(db, "perf-enqueue", o => o.Worker.Enabled = false);
        try
        {
            int jobs = PerfScale.Jobs;
            NoOpPayload[] payloads = [.. Enumerable.Range(0, jobs).Select(_ => new NoOpPayload())];

            // Warm-up (connection pool, plan cache, JIT).
            await host.EnqueueManyAsync(payloads[..500]);

            Stopwatch watch = Stopwatch.StartNew();
            IReadOnlyList<JobId> ids = await host.EnqueueManyAsync(payloads);
            double bulkRate = jobs / watch.Elapsed.TotalSeconds;
            ids.Should().HaveCount(jobs);

            // Four callers at once (four API instances fanning out).
            watch.Restart();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.EnqueueManyAsync(payloads[..(jobs / 4)])));
            double parallelRate = jobs / 4 * 4 / watch.Elapsed.TotalSeconds;

            // One job per call, sequentially (the EnqueueAsync path).
            const int Singles = 1_000;
            watch.Restart();
            for (int i = 0; i < Singles; i++)
            {
                await host.EnqueueAsync(new NoOpPayload());
            }

            double singleRate = Singles / watch.Elapsed.TotalSeconds;
            int total = await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs");
            int queueRows = await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue");
            total.Should().Be(500 + jobs + jobs / 4 * 4 + Singles);
            queueRows.Should().Be(total, "every enqueued job has its queue row");

            PerfReport.Write(output, kind, $"bulk enqueue (EnqueueManyAsync, {jobs} jobs, 1 caller)", $"{bulkRate:F0} jobs/s", $">= {TargetJobsPerSecond} jobs/s", bulkRate >= TargetJobsPerSecond);
            PerfReport.Write(output, kind, "bulk enqueue, 4 parallel callers", $"{parallelRate:F0} jobs/s", "reported", true);
            PerfReport.Write(output, kind, "single EnqueueAsync, sequential", $"{singleRate:F0} jobs/s", "reported", true);
            bulkRate.Should().BeGreaterThanOrEqualTo(TargetJobsPerSecond, "§14.8: bulk enqueue (EnqueueManyAsync) is at least 5 000 jobs/s");
        }
        finally
        {
            await host.DisposeAsync();
            db.ClearPools();
        }
    }
}