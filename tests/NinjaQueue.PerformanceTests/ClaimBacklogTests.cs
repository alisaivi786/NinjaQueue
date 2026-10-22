using NinjaQueue.Storage;

namespace NinjaQueue.PerformanceTests;

/// <summary>
/// §14.8 p95 claim latency &lt; 20 ms at 0, 100K and 1M pending, and the claim's index behaviour at scale (the plan must
/// use <c>ix_job_queue_claim_type</c> at 100K and 1M; small tables may scan, §15.12). The measured call is the whole store claim
/// (<c>IJobStore.ClaimAsync</c>, one job: claim statement + attempt row + claimed event in one transaction), which bounds
/// the claim query from above.
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class ClaimBacklogTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private const double TargetP95Ms = 20;
    private const int Samples = 200;
    private const string ForeignType = "Perf.Foreign.v1";

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Claim_p95_stays_below_20_ms_at_0_100K_and_1M_pending(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        PerfHost host = PerfHost.Build(db, "perf-claim", o => o.Worker.Enabled = false);
        List<string> failures = [];
        try
        {
            int seeded = 0;
            foreach (int backlog in ((int[])[0, 100_000, PerfScale.MaxBacklog]).Distinct())
            {
                if (backlog > seeded)
                {
                    Stopwatch seed = Stopwatch.StartNew();
                    await Seeder.SeedPendingAsync(db, NoOpHandler.Code, "default", backlog - seeded);
                    PerfReport.Note(output, kind, $"seeded {backlog - seeded} pending rows in {seed.Elapsed.TotalSeconds:F0} s");
                    seeded = backlog;
                }

                (Percentiles one, Percentiles ten) = await MeasureClaimsAsync(host);
                string plan = await DbProbes.ClaimPlanAsync(db, "default", NoOpHandler.Code);
                IReadOnlyList<string> indexes = DbProbes.QueueIndexesIn(plan);
                bool latencyOk = one.P95 < TargetP95Ms;
                bool planOk = backlog < 100_000 || indexes.Contains("ix_job_queue_claim_type");
                PerfReport.Write(output, kind, $"claim latency (1 job), {backlog} pending, ms", one.ToString(), $"p95 < {TargetP95Ms} ms", latencyOk);
                PerfReport.Write(output, kind, $"claim latency (10 jobs), {backlog} pending, ms", ten.ToString(), "reported", true);
                PerfReport.Write(output, kind, $"claim plan, {backlog} pending", $"indexes [{string.Join(", ", indexes)}]",
                    backlog < 100_000 ? "reported (small table may scan)" : "uses ix_job_queue_claim_type", planOk);
                output.WriteLine(DbProbes.Summarize(db, plan));
                if (!latencyOk)
                {
                    failures.Add($"{backlog} pending: p95 {one.P95:F1} ms");
                }

                if (!planOk)
                {
                    failures.Add($"{backlog} pending: plan does not use ix_job_queue_claim_type");
                }

                seeded = await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue WHERE lease_token IS NULL");
            }
        }
        finally
        {
            await host.DisposeAsync();
            db.ClearPools();
        }

        failures.Should().BeEmpty("§14.8: p95 claim latency < 20 ms at 0, 100K and 1M pending, on the claim index");
    }

    /// <summary>
    /// A rolling deploy or an orphaned type can leave a large backlog of a type this worker does not run at the head of the
    /// queue (§14.3). The claim filters job types after the index range, so it must still find the runnable jobs quickly.
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Claim_p95_stays_below_20_ms_behind_a_1M_backlog_of_another_job_type(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        PerfHost host = PerfHost.Build(db, "perf-claim-foreign", o => o.Worker.Enabled = false);
        try
        {
            await Seeder.SeedPendingAsync(db, ForeignType, "default", PerfScale.MaxBacklog, readySecondsAgo: 7200);
            await Seeder.SeedPendingAsync(db, NoOpHandler.Code, "default", 2_000, readySecondsAgo: 60);
            (Percentiles one, _) = await MeasureClaimsAsync(host);
            string plan = await DbProbes.ClaimPlanAsync(db, "default", NoOpHandler.Code);
            PerfReport.Write(output, kind, $"claim latency (1 job) behind {PerfScale.MaxBacklog} foreign-type rows, ms", one.ToString(), $"p95 < {TargetP95Ms} ms", one.P95 < TargetP95Ms);
            output.WriteLine(DbProbes.Summarize(db, plan));
            one.P95.Should().BeLessThan(TargetP95Ms, "§14.8: p95 claim latency < 20 ms at 1M pending, whatever their type");
        }
        finally
        {
            await host.DisposeAsync();
            db.ClearPools();
        }
    }

    private static async Task<(Percentiles One, Percentiles Ten)> MeasureClaimsAsync(PerfHost host)
    {
        for (int i = 0; i < 20; i++)
        {
            await host.ClaimAsync(NoOpHandler.Code, 1);
        }

        List<double> one = [];
        for (int i = 0; i < Samples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<ClaimedJob> _ = await host.ClaimAsync(NoOpHandler.Code, 1);
            one.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        List<double> ten = [];
        for (int i = 0; i < 50; i++)
        {
            long start = Stopwatch.GetTimestamp();
            await host.ClaimAsync(NoOpHandler.Code, 10);
            ten.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return (new Percentiles(one), new Percentiles(ten));
    }
}