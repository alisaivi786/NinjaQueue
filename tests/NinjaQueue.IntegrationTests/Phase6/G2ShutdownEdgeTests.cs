using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace NinjaQueue.IntegrationTests.Phase6;

/// <summary>
/// Gate G2 edges (§16.2, §16.3): a fenced step that ignores the shutdown token never commits late (its open transaction is ended
/// on the server, the kept lease expires and recovery charges the attempt, and the business write lands exactly once, from the
/// retry); and a SIGTERM-style stop (<see cref="IHostApplicationLifetime.StopApplication"/> on a host running as in
/// <c>Program.Main</c>) completes inside <c>HostOptions.ShutdownTimeout</c> = the §16.2 budget, below a 35 s container grace period.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G2")]
public sealed class G2ShutdownEdgeTests(JobsDatabaseFixture fixture)
{
    private const string LongFenced = "Test.Fenced.Long.v1";

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Fenced_step_ignoring_the_shutdown_token_never_commits_late_and_is_recovered_charged(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<LongFencedProbeHandler>(), o =>
        {
            o.Worker.LeaseDuration = TimeSpan.FromSeconds(10);
            o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(2);
            o.Worker.ShutdownDrainPeriod = TimeSpan.FromSeconds(1);
            o.Worker.ShutdownCancelPeriod = TimeSpan.FromSeconds(1.5);
            o.Worker.ShutdownCleanupPeriod = TimeSpan.FromSeconds(2);
            o.Worker.MaintenanceInterval = TimeSpan.FromSeconds(30);
        });
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int> session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                session.TrySetResult(Convert.ToInt32(await step.ScalarAsync(db.IsPostgres ? "SELECT pg_backend_pid()" : "SELECT @@SPID", ct), System.Globalization.CultureInfo.InvariantCulture));
                await release.Task; // ignores every token, including the shutdown token
            }

            return Step.Complete();
        };

        await host.StartAsync();
        JobId id = await host.EnqueueAsync(new LongFencedPayload("fenced-ignore"));
        int sessionId = await session.Task.WaitAsync(TimeSpan.FromSeconds(20));

        TimeSpan stopped = await host.StopAsync();

        stopped.Should().BeLessThan(TimeSpan.FromSeconds(1 + 1.5 + 2 + 1), "an ignored token cannot stretch the budget");
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Processing);
        // The open step still holds the queue row lock, which blocks a plain read under RCSI OFF: read the lease without locks.
        long rowId = await db.JobColumnAsync<long>(id, "id");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_queue{(db.IsPostgres ? "" : " WITH (NOLOCK)")} WHERE job_id = {rowId} AND lease_token IS NOT NULL"))
            .Should().Be(1, "the abandoned step keeps its lease");
        (await db.WidgetCountAsync("fenced-ignore")).Should().Be(0, "the step's write is uncommitted");

        // The open transaction is ended on the server: PostgreSQL by its idle-in-transaction bound (StepTimeout 8 s), SQL Server by
        // the watchdog once the step is past its deadline.
        if (db.IsPostgres)
        {
            (await JobProbes.EventuallyAsync(async () => await db.ScalarAsync<int>($"SELECT count(*) FROM pg_stat_activity WHERE pid = {sessionId}") == 0, TimeSpan.FromSeconds(20)))
                .Should().BeTrue();
        }
        else
        {
            int kills = 0;
            (await JobProbes.EventuallyAsync(async () => (kills += await host.KillOverdueAsync(TimeSpan.Zero)) > 0, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250)))
                .Should().BeTrue();
            (await db.EventsOfAsync(id)).Should().Contain(JobEventCodes.FencedStepKilled);
        }

        (await JobProbes.EventuallyAsync(async () => (await db.QueueStateAsync(id))!.Value.Expired, TimeSpan.FromSeconds(15))).Should().BeTrue();
        (await host.RecoverAsync()).Should().Be(1);
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Scheduled);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3), "the abandoned attempt is charged");
        (await db.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.LeaseLost);
        (await db.WidgetCountAsync("fenced-ignore")).Should().Be(0);

        // The handler finally returns: nothing commits late (the transaction is gone and the lease belongs to recovery).
        release.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await db.WidgetCountAsync("fenced-ignore")).Should().Be(0, "a late step never commits");
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Scheduled);
        (await db.EventsOfAsync(id)).Should().NotContain(JobEventCodes.JobSucceeded);

        await db.MakeAvailableNowAsync(id);
        (await host.RunOneAsync(LongFenced)).Should().Be(Execution.ExecutionResult.Succeeded);
        (await db.WidgetCountAsync("fenced-ignore")).Should().Be(1, "the business write lands exactly once, from the retry");
        (await db.AttemptsOfAsync(id)).Should().Be((2, 2, 3));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task SIGTERM_style_stop_finishes_within_the_container_grace_period_and_cleans_up(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);

        // Production defaults (§16.2): drain 12 s + cancel 10 s + cleanup 5 s inside HostOptions.ShutdownTimeout = 30 s.
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o =>
        {
            o.Worker.ShutdownDrainPeriod = TimeSpan.FromSeconds(12);
            o.Worker.ShutdownCancelPeriod = TimeSpan.FromSeconds(10);
            o.Worker.ShutdownCleanupPeriod = TimeSpan.FromSeconds(5);
        }, services: s => s.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30)));
        TaskCompletionSource ignoring = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbePlan plan = host.Get<ProbePlan>();
        plan.Behaviour = async (payload, _, ct) =>
        {
            if (payload.Name == "coop")
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                await ignoring.Task;
            }

            return JobResult.Success();
        };
        IHostApplicationLifetime lifetime = host.Get<IHostApplicationLifetime>();

        Task running = host.Host.RunAsync(); // as Program.Main does: start, then wait for the stop signal
        await JobProbes.EventuallyAsync(() => Task.FromResult(lifetime.ApplicationStarted.IsCancellationRequested), TimeSpan.FromSeconds(20));
        string workerId = host.Worker.WorkerId;
        JobId coop = await host.EnqueueAsync(new ProbePayload("coop"));
        JobId ignore = await host.EnqueueAsync(new ProbePayload("ignore"));
        await Task.WhenAll(plan.Started("coop"), plan.Started("ignore")).WaitAsync(TimeSpan.FromSeconds(20));
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_workers WHERE worker_id = '{workerId}'")).Should().Be(1);

        long signalled = Stopwatch.GetTimestamp();
        lifetime.StopApplication(); // what SIGTERM from `docker stop` triggers
        await running.WaitAsync(TimeSpan.FromSeconds(45));
        TimeSpan stop = Stopwatch.GetElapsedTime(signalled);

        stop.Should().BeLessThan(TimeSpan.FromSeconds(30), "the whole stop fits HostOptions.ShutdownTimeout, inside a stop_grace_period of 35 s (§16.2)");
        stop.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(21.5), "the ignored token holds the stop for drain + cancel");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_leases WHERE owner = '{workerId}' AND expires_at_utc > {db.Now}")).Should().Be(0, "maintenance leases released");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_workers WHERE worker_id = '{workerId}'")).Should().Be(0, "worker row removed");
        (await db.StatusOfAsync(coop)).Should().Be(JobStatus.Enqueued, "the cooperative job was released");
        (await db.AttemptsOfAsync(coop)).Should().Be((0, 0, 3));
        (await db.QueueStateAsync(ignore))!.Value.Leased.Should().BeTrue("the ignoring job keeps its lease for recovery");
        ignoring.SetResult();
    }
}