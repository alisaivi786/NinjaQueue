using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase6;

/// <summary>
/// Gate G2 (§16.2, §14.1): a real <c>IHost</c> with workers is stopped with <c>StopAsync</c> while five handlers run, on the
/// standard and the fenced path. A short job finishes during the drain (Succeeded); a long job that honours the token is
/// Released (attempt refunded, lease cleared, fenced writes rolled back); a job that ignores the token keeps its lease, which is
/// no longer renewed, expires and is recovered charged. The stop fits the budget, the maintenance leases are released and the
/// worker row is removed.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G2")]
public sealed class G2GracefulShutdownTests(JobsDatabaseFixture fixture)
{
    private static readonly TimeSpan Drain = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Cancel = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Cleanup = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(10);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stop_drains_short_jobs_releases_cooperative_ones_and_abandons_the_rest_within_the_budget(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>().AddJobHandler<LongFencedProbeHandler>(), o =>
        {
            o.Worker.Queues["default"] = 8;
            o.Worker.LeaseDuration = Lease;
            o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(2);
            o.Worker.ShutdownDrainPeriod = Drain;
            o.Worker.ShutdownCancelPeriod = Cancel;
            o.Worker.ShutdownCleanupPeriod = Cleanup;
            o.Worker.MaintenanceInterval = TimeSpan.FromSeconds(30);
        });
        TaskCompletionSource ignoring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbePlan plan = host.Get<ProbePlan>();
        plan.Behaviour = async (payload, _, ct) =>
        {
            switch (payload.Name)
            {
                case "std-short":
                    await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
                    break;
                case "std-coop":
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                default:
                    await ignoring.Task; // ignores the token entirely
                    break;
            }

            return JobResult.Success();
        };
        FencedProbePlan fenced = host.Get<FencedProbePlan>();
        fenced.Step = async (payload, _, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            await Task.Delay(payload.Name == "fenced-short" ? TimeSpan.FromMilliseconds(700) : Timeout.InfiniteTimeSpan, payload.Name == "fenced-short" ? CancellationToken.None : ct);
            return Step.Complete();
        };

        await host.StartAsync();
        string workerId = host.Worker.WorkerId;
        JobId stdShort = await host.EnqueueAsync(new ProbePayload("std-short"));
        JobId stdCoop = await host.EnqueueAsync(new ProbePayload("std-coop"));
        JobId stdIgnore = await host.EnqueueAsync(new ProbePayload("std-ignore"));
        JobId fencedShort = await host.EnqueueAsync(new LongFencedPayload("fenced-short"));
        JobId fencedCoop = await host.EnqueueAsync(new LongFencedPayload("fenced-coop"));
        await Task.WhenAll(plan.Started("std-short"), plan.Started("std-coop"), plan.Started("std-ignore"), fenced.Started("fenced-short"), fenced.Started("fenced-coop"))
            .WaitAsync(TimeSpan.FromSeconds(20));

        TimeSpan stopped = await host.StopAsync();

        stopped.Should().BeLessThan(Drain + Cancel + Cleanup + TimeSpan.FromSeconds(1), "the stop fits the single shutdown budget (§16.2)");
        stopped.Should().BeGreaterThanOrEqualTo(Drain + Cancel - TimeSpan.FromMilliseconds(200), "a handler ignoring the token holds the stop until the cancel window ends");

        // Finished during the drain.
        (await db.StatusOfAsync(stdShort)).Should().Be(JobStatus.Succeeded);
        (await db.StatusOfAsync(fencedShort)).Should().Be(JobStatus.Succeeded);
        (await db.WidgetCountAsync("fenced-short")).Should().Be(1);

        // Cooperated with the shutdown token: Released, refunded, lease cleared, available again.
        foreach (JobId released in new[] { stdCoop, fencedCoop })
        {
            (await db.StatusOfAsync(released)).Should().Be(JobStatus.Enqueued);
            (await db.AttemptsOfAsync(released)).Should().Be((0, 0, 3), "a shutdown release is refunded ({0})", await db.AttemptTrailAsync(released));
            (await db.AttemptOutcomesAsync(released)).Should().Equal(JobAttemptOutcome.Released);
            (await db.QueueStateAsync(released)).Should().Be((false, false, 3), "the lease is cleared");
            (await db.EventsOfAsync(released)).Should().EndWith(JobEventCodes.JobReleased);
        }

        (await db.WidgetCountAsync("fenced-coop")).Should().Be(0, "the in-flight fenced step rolled back");

        // Ignored the token: abandoned with its lease, which is no longer renewed.
        (await db.StatusOfAsync(stdIgnore)).Should().Be(JobStatus.Processing);
        (await db.QueueStateAsync(stdIgnore))!.Value.Leased.Should().BeTrue("an abandoned lease is not cleared: we cannot prove the handler stopped");
        (await JobProbes.EventuallyAsync(async () => (await db.QueueStateAsync(stdIgnore))!.Value.Expired, Lease + TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the abandoned lease is no longer renewed, so it expires");
        (await host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().RecoverExpiredLeasesAsync(10, _ => TimeSpan.Zero, CancellationToken.None))).Should().Be(1);
        (await db.StatusOfAsync(stdIgnore)).Should().Be(JobStatus.Scheduled);
        (await db.AttemptsOfAsync(stdIgnore)).Should().Be((1, 1, 3), "the only shutdown path that costs an attempt");
        (await db.AttemptOutcomesAsync(stdIgnore)).Should().Equal(JobAttemptOutcome.LeaseLost);

        // Cleanup: maintenance leases released, worker row removed.
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_leases WHERE owner = '{workerId}' AND expires_at_utc > {db.Now}")).Should().Be(0);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_leases WHERE owner = '{workerId}'")).Should().BeGreaterThan(0, "the leases were held before the stop");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_workers WHERE worker_id = '{workerId}'")).Should().Be(0);

        // The abandoned handler finally returns: its late completion is discarded.
        ignoring.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        (await db.StatusOfAsync(stdIgnore)).Should().Be(JobStatus.Scheduled);
        (await db.EventsOfAsync(stdIgnore)).Should().NotContain(JobEventCodes.JobSucceeded);
    }
}