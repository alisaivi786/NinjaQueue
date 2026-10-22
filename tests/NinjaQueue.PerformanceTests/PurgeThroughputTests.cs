using NinjaQueue.Storage;

namespace NinjaQueue.PerformanceTests;

/// <summary>
/// Purge throughput (§14.7, §14.8 "job_queue / tables do not grow"). §14.8 has no explicit purge figure, so the target is
/// derived: retention must keep up with the sustained intake target, i.e. purge ≥ 500 jobs/s with default batch size
/// (children first, both tiers). Also reports the longest single purge call (how long one round holds its locks).
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class PurgeThroughputTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private const double TargetJobsPerSecond = 500;

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Purge_keeps_up_with_the_sustained_throughput_target(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        int jobs = PerfScale.PurgeJobs;

        // Past both tiers: operational (Succeeded 14 d) and audit (400 d).
        await Seeder.SeedSucceededAsync(db, NoOpHandler.Code, jobs, completedDaysAgo: 401);
        PerfHost host = PerfHost.Build(db, "perf-purge", o => o.Worker.Enabled = false);
        NinjaQueueRetentionOptions retention = new();
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            List<double> calls = [];
            long rows = 0;
            for (int round = 0; round < 10_000; round++)
            {
                long start = Stopwatch.GetTimestamp();
                int affected = await host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().PurgeAsync(retention, CancellationToken.None));
                calls.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                rows += affected;
                if (affected == 0)
                {
                    break;
                }
            }

            TimeSpan took = watch.Elapsed;
            double rate = jobs / took.TotalSeconds;
            Percentiles perCall = new(calls);
            PerfReport.Write(output, kind, $"purge, {jobs} jobs (1 attempt + 3 events each), batch {retention.PurgeBatchSize}",
                $"{rate:F0} jobs/s, {rows / took.TotalSeconds:F0} rows/s, {calls.Count} calls in {took.TotalSeconds:F1} s", $">= {TargetJobsPerSecond} jobs/s (derived)", rate >= TargetJobsPerSecond);
            PerfReport.Write(output, kind, "purge call duration, ms", perCall.ToString(), "reported", true);

            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(0);
            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_attempts")).Should().Be(0);
            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_events")).Should().Be(0);
            rate.Should().BeGreaterThanOrEqualTo(TargetJobsPerSecond, "retention must keep up with the 500 jobs/s sustained intake");
        }
        finally
        {
            await host.DisposeAsync();
            db.ClearPools();
        }
    }
}