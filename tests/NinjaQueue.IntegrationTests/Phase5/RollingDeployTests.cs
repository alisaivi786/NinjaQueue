using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Phase 5 exit, rolling deploy (§14.3): real hosts sharing one database. A worker that does not advertise a type never claims
/// it; orphan detection flags it after <c>UnknownTypeOrphanAfter</c> (short test options) and it stays Enqueued; the flag
/// clears once a live worker advertises the type, and a capable worker then runs it. The runtime safety net defers (refunds).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "5")]
public sealed class RollingDeployTests(JobsDatabaseFixture fixture)
{
    private const string NewType = "Test.Rolling.NewType.v1";

    private static void Short(NinjaQueueOptions o)
    {
        o.Worker.UnknownTypeOrphanAfter = TimeSpan.FromSeconds(1);
        o.Worker.MaintenanceInterval = TimeSpan.FromMilliseconds(300);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Old_worker_never_claims_a_new_type_which_is_orphaned_then_run_by_a_capable_worker(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost oldBuild = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), Short);
        await using WorkerTestHost enqueuer = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>().AddJobHandler<RollingNewTypeHandler>(), o =>
        {
            Short(o);
            o.Worker.Enabled = false;
        });
        await oldBuild.StartAsync();

        JobId x1 = await enqueuer.EnqueueAsync(new RollingPayload("x1"));
        JobId ping = await enqueuer.EnqueueAsync(new ProbePayload("ping"));

        // The old worker is live and claiming (it runs the type it knows) ...
        (await JobProbes.EventuallyAsync(async () => await db.StatusOfAsync(ping) == JobStatus.Succeeded, TimeSpan.FromSeconds(15))).Should().BeTrue();

        // ... and flags the type it does not know, without ever claiming it.
        (await JobProbes.EventuallyAsync(async () => !await db.IsNullAsync(x1, "orphaned_at_utc"), TimeSpan.FromSeconds(15))).Should().BeTrue();
        JobId x2 = await enqueuer.EnqueueAsync(new RollingPayload("x2"));
        (await JobProbes.EventuallyAsync(async () => !await db.IsNullAsync(x2, "orphaned_at_utc"), TimeSpan.FromSeconds(15))).Should().BeTrue();
        await Task.Delay(TimeSpan.FromSeconds(1)); // a few more maintenance rounds

        foreach (JobId x in new[] { x1, x2 })
        {
            (await db.StatusOfAsync(x)).Should().Be(JobStatus.Enqueued, "an orphan is never dead-lettered and stays claimable");
            (await db.AttemptsOfAsync(x)).Should().Be((0, 0, 5), "no attempt is charged by a worker that cannot run it");
            (await db.EventsOfAsync(x)).Should().Equal([JobEventCodes.JobEnqueued, JobEventCodes.JobOrphaned], "one JobOrphaned event per flagging, never a claim");
        }

        // A live worker now advertises the type: the flag clears (the old leader's maintenance sees it).
        await oldBuild.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().HeartbeatAsync(
            new WorkerRegistration("new-build-booting", "host", 1, null, ["default"], [NewType]), CancellationToken.None));
        (await JobProbes.EventuallyAsync(async () => await db.IsNullAsync(x1, "orphaned_at_utc"), TimeSpan.FromSeconds(15))).Should().BeTrue();
        (await db.EventsOfAsync(x1)).Should().EndWith(JobEventCodes.JobOrphanCleared);
        (await db.StatusOfAsync(x1)).Should().Be(JobStatus.Enqueued);

        // A capable worker runs it.
        await using WorkerTestHost newBuild = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>().AddJobHandler<RollingNewTypeHandler>(), Short);
        await newBuild.StartAsync();
        (await JobProbes.EventuallyAsync(async () => await db.StatusOfAsync(x1) == JobStatus.Succeeded && await db.StatusOfAsync(x2) == JobStatus.Succeeded, TimeSpan.FromSeconds(20))).Should().BeTrue();
        (await db.AttemptsOfAsync(x1)).Should().Be((1, 1, 5));
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id IN ('{x1.Value}', '{x2.Value}') AND e.event_code = '{JobEventCodes.JobClaimed}' AND e.worker_id = '{oldBuild.Worker.WorkerId}'"))
            .Should().Be(0, "the old worker never claimed the new type");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Claimed_code_unknown_to_the_registry_is_deferred_and_refunded(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost newBuild = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>().AddJobHandler<RollingNewTypeHandler>(), o => o.Worker.Enabled = false);
        await using WorkerTestHost oldBuild = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.Enabled = false);
        JobId id = await newBuild.EnqueueAsync(new RollingPayload("deferred"));

        // Safety net (§14.3 rule 5): the old build somehow holds a claim on a code its registry does not know.
        ClaimedJob job = (await newBuild.ClaimOneAsync(NewType))!;
        using JobSignals signals = new(CancellationToken.None);
        (await oldBuild.Get<JobExecutor>().ExecuteAsync(job, signals)).Should().Be(ExecutionResult.Rescheduled);

        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Enqueued, "never dead-lettered for an unknown code");
        (await db.AttemptsOfAsync(id)).Should().Be((0, 0, 5), "a deferral is refunded");
        (await db.JobColumnAsync<int>(id, "release_count")).Should().Be(1);
        (await db.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Deferred);
        (await db.EventsOfAsync(id)).Should().EndWith(JobEventCodes.JobDeferred);
        (await newBuild.ClaimOneAsync(NewType)).Should().BeNull("a deferred job waits 30 s");
    }
}