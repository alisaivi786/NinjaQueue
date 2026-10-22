using System.Runtime.CompilerServices;

namespace NinjaQueue.PerformanceTests;

/// <summary>
/// §14.8 sustained throughput (no-op handler, ≥ 500 jobs/s with 4 instances), claim throughput with N instances, the
/// connection budget (≤ Σ queue concurrency + maintenance + 2 per instance, never above the pool max) and the
/// <c>job_queue</c> no-growth target (the queue is the backlog only; terminal jobs leave it).
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class ThroughputTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private const int Concurrency = 16;
    private const int PoolMax = 100;
    private const double TargetJobsPerSecond = 500;
    private const int TargetInstances = 4;

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Sustained_no_op_throughput_scales_with_instances_within_the_connection_budget(DatabaseKind kind)
    {
        int jobs = PerfScale.Jobs;
        Dictionary<int, double> rates = [];
        List<string> budgetViolations = [];
        foreach (int instances in (int[])[1, 2, TargetInstances, 8])
        {
            PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
            if (instances == 1)
            {
                PerfReport.Note(output, kind, $"server: {await DbProbes.ServerSettingsAsync(db)}");
            }

            await Seeder.SeedPendingAsync(db, NoOpHandler.Code, "default", jobs, readySecondsAgo: 5);
            List<PerfHost> hosts = [.. Enumerable.Range(0, instances).Select(i => PerfHost.Build(db, $"perf-w{i}", o => o.Worker.Queues = new(StringComparer.Ordinal) { ["default"] = Concurrency }))];
            Dictionary<string, int> maxConnections = hosts.ToDictionary(h => h.Name, _ => 0, StringComparer.Ordinal);
            StrongBox<bool> sampling = new(true);
            try
            {
                Task sampler = SampleConnectionsAsync(db, maxConnections, sampling);
                await Task.WhenAll(hosts.Select(h => h.StartWorkersAsync()));
                // Mid-run, capture the claim plan under churn (dead index entries, buffers read) as evidence for the numbers.
                string? midRunPlan = null;
                await Wait.UntilAsync(async () =>
                {
                    int pending = await db.PendingAsync();
                    if (midRunPlan is null && instances == TargetInstances && pending <= jobs / 2)
                    {
                        midRunPlan = DbProbes.Summarize(db, await DbProbes.ClaimPlanAsync(db, "default", NoOpHandler.Code));
                    }

                    return pending == 0;
                }, TimeSpan.FromMinutes(15), 250);
                if (midRunPlan is not null)
                {
                    PerfReport.Note(output, kind, $"claim plan mid-run (4 instances): {midRunPlan.ReplaceLineEndings(" / ")}");
                }

                sampling.Value = false;
                await sampler;

                // Throughput from the database clock: first claim to last completion.
                double windowMs = await db.ScalarAsync<double>(
                    $"SELECT {db.MsBetween("MIN(started_at_utc)", "MAX(completed_at_utc)")} FROM ninjaqueue.background_jobs WHERE status = 10");
                double rate = jobs / (windowMs / 1000.0);
                rates[instances] = rate;
                (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = 10")).Should().Be(jobs, "every job succeeded");

                int bound = Concurrency + 1 + 2;
                foreach ((string name, int max) in maxConnections)
                {
                    if (max > bound || max > PoolMax)
                    {
                        budgetViolations.Add($"{instances} instances: {name} held {max} sessions (bound {bound}, pool max {PoolMax})");
                    }
                }

                PerfReport.Write(output, kind, $"no-op throughput, {instances} instance(s) x {Concurrency}", $"{rate:F0} jobs/s ({jobs} jobs in {windowMs / 1000:F1} s)",
                    instances == TargetInstances ? $">= {TargetJobsPerSecond} jobs/s" : "reported (target applies at 4 instances)",
                    instances != TargetInstances || rate >= TargetJobsPerSecond);
                PerfReport.Write(output, kind, $"DB sessions per instance, {instances} instance(s)", $"max {maxConnections.Values.Max()}",
                    $"<= {bound} (16 + maintenance + 2), <= pool max {PoolMax}", maxConnections.Values.Max() <= bound);

                // job_queue is the backlog only: empty once everything is terminal (§14.8 "no growth").
                int queueRows = await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue");
                string vacuum = db.IsPostgres ? await PostgresQueueBloatAsync(db) : "";
                PerfReport.Write(output, kind, $"job_queue rows after {jobs} processed, {instances} instance(s)", $"{queueRows}{vacuum}", "0 (backlog only)", queueRows == 0);
                queueRows.Should().Be(0);
            }
            finally
            {
                sampling.Value = false;
                await PerfHost.DisposeAllAsync(hosts);
                db.ClearPools();
            }
        }

        PerfReport.Note(output, kind, "claim throughput scaling: " + string.Join(", ", rates.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Key} inst = {r.Value:F0}/s"))));
        budgetViolations.Should().BeEmpty("every instance stays within Σ(queue concurrency) + maintenance + 2 sessions (§14.8)");
        rates[TargetInstances].Should().BeGreaterThanOrEqualTo(TargetJobsPerSecond, "§14.8: sustained throughput with a no-op handler is at least 500 jobs/s");
    }

    private static async Task SampleConnectionsAsync(PerfDatabase db, Dictionary<string, int> max, StrongBox<bool> running)
    {
        while (Volatile.Read(ref running.Value))
        {
            foreach (string name in max.Keys.ToList())
            {
                int n = await DbProbes.ConnectionsAsync(db, name);
                if (n > max[name])
                {
                    max[name] = n;
                }
            }

            await Task.Delay(250);
        }
    }

    private static async Task<string> PostgresQueueBloatAsync(PerfDatabase db)
    {
        object?[] before = (await db.QueryAsync("""
            SELECT n_dead_tup, pg_relation_size('ninjaqueue.job_queue'), pg_indexes_size('ninjaqueue.job_queue')
              FROM pg_stat_user_tables WHERE schemaname = 'jobs' AND relname = 'job_queue'
            """))[0];
        await db.ExecuteAsync("VACUUM ninjaqueue.job_queue");
        object?[] after = (await db.QueryAsync("""
            SELECT n_dead_tup, pg_relation_size('ninjaqueue.job_queue'), pg_indexes_size('ninjaqueue.job_queue')
              FROM pg_stat_user_tables WHERE schemaname = 'jobs' AND relname = 'job_queue'
            """))[0];
        return string.Create(CultureInfo.InvariantCulture,
            $" (dead tuples {before[0]} -> {after[0]} after VACUUM; heap {before[1]} B, indexes {before[2]} B -> {after[2]} B)");
    }
}