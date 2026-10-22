using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Phase 5 exit (§7.4, §14.1, §14.2): kill mid-job (a claim abandoned without an outcome is recovered after its lease expires,
/// charged), lease loss (a late completion is rejected and writes nothing), a crash after an idempotent handler's own write
/// re-runs safely, and crash loops stay bounded. PostgreSQL and SQL Server RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "5")]
public sealed class RecoveryTests(JobsDatabaseFixture fixture)
{
    private const string Probe = "Test.Probe.v1";

    private Task<JobsHost> HostAsync(DatabaseKind kind)
        => JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            b.Services.AddSingleton<ProbePlan>();
        });

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Kill_mid_job_is_recovered_after_the_lease_expires_with_the_attempt_charged(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await host.EnqueueAsync(new ProbePayload("crash"));

        // The worker claims and dies: no handler result, no outcome, no renewal.
        ClaimedJob crashed = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromSeconds(1)))!;
        (await host.RecoverAsync()).Should().Be(0, "the lease has not expired yet");
        (await JobProbes.EventuallyAsync(async () => (await host.Database.QueueStateAsync(id))!.Value.Expired, TimeSpan.FromSeconds(10))).Should().BeTrue();

        (await host.RecoverAsync()).Should().Be(1);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "a crash is charged (§14.1): crash loops stay bounded");
        (await host.Database.QueueStateAsync(id)).Should().Be((false, false, 2), "the lease is cleared and the job is claimable again");
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.LeaseLost);
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobLeaseExpired);
        (await host.RecoverAsync()).Should().Be(0, "recovery acts once");

        // The dead worker's late outcome is refused: the lease token it holds is gone.
        (await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RecordOutcomeAsync(crashed, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)))
            .Should().Be(OutcomeResult.LeaseLost);

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.Succeeded);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.LeaseLost, JobAttemptOutcome.Succeeded);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Crash_loop_dead_letters_at_max_attempts_and_a_pending_cancel_wins_recovery(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId poison = await host.EnqueueAsync(new ProbePayload("poison"));
        for (int i = 1; i <= 3; i++)
        {
            ClaimedJob job = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;
            await host.Database.ExpireLeaseAsync(job.Id);
            (await host.RecoverAsync()).Should().Be(1);
        }

        (await host.StatusAsync(poison)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(poison)).Should().Be((3, 3, 3));
        (await host.Database.JobColumnAsync<short>(poison, "terminal_reason_code")).Should().Be((short)JobTerminalReason.Exhausted);
        (await host.CountQueueAsync(poison)).Should().Be(0);

        JobId cancelled = await host.EnqueueAsync(new ProbePayload("cancel-pending"));
        ClaimedJob running = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'stop' WHERE id = {running.Id}");
        await host.Database.ExpireLeaseAsync(running.Id);

        (await host.RecoverAsync()).Should().Be(1);

        (await host.StatusAsync(cancelled)).Should().Be(JobStatus.Cancelled, "a job with a pending cancel is never rescheduled (§14.4 step 5)");
        (await host.CountQueueAsync(cancelled)).Should().Be(0);
        (await host.EventsAsync(cancelled)).Should().EndWith(JobEventCodes.JobCancelled);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_lost_mid_run_rejects_the_late_completion_and_writes_nothing(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbePlan plan = host.Get<ProbePlan>();
        plan.Behaviour = async (_, _, _) =>
        {
            await proceed.Task;
            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new ProbePayload("lost"));
        ClaimedJob job = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;

        Task<ExecutionResult> run = host.ExecuteAsync(job);
        await plan.Started("lost").WaitAsync(TimeSpan.FromSeconds(10));
        Guid otherOwner = Guid.NewGuid();
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET lease_token = '{otherOwner}', lease_owner = 'another-worker' WHERE job_id = {job.Id}");
        proceed.SetResult();

        (await run).Should().Be(ExecutionResult.LeaseLost);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "the new owner's state is untouched");
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed);
        (await host.Database.AttemptOutcomesAsync(id)).Should().ContainSingle().Which.Should().BeNull("the old owner closed nothing");
        (await host.Database.ScalarAsync<string>($"SELECT CAST(lease_token AS varchar(36)) FROM ninjaqueue.job_queue WHERE job_id = {job.Id}"))
            .Should().BeEquivalentTo(otherOwner.ToString());
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Progress_report_after_lease_loss_stops_the_handler(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool continuedAfterProgress = false;
        ProbePlan plan = host.Get<ProbePlan>();
        plan.Behaviour = async (_, context, ct) =>
        {
            await context.ReportProgressAsync(1, 10, ct);
            await proceed.Task;
            await context.ReportProgressAsync(2, 10, ct);
            continuedAfterProgress = true;
            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new ProbePayload("progress"));
        ClaimedJob job = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;

        Task<ExecutionResult> run = host.ExecuteAsync(job);
        await plan.Started("progress").WaitAsync(TimeSpan.FromSeconds(10));
        (await JobProbes.EventuallyAsync(async () => await host.Database.JobColumnAsync<int?>(id, "progress_processed") == 1, TimeSpan.FromSeconds(10))).Should().BeTrue();
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET lease_token = '{Guid.NewGuid()}' WHERE job_id = {job.Id}");
        proceed.SetResult();

        (await run).Should().Be(ExecutionResult.LeaseLost);

        continuedAfterProgress.Should().BeFalse("the progress call fails fast once the lease is gone");
        (await host.Database.JobColumnAsync<int?>(id, "progress_processed")).Should().Be(1, "the second progress write never landed");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Crash_after_an_idempotent_handlers_own_write_re_runs_safely(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        TaskCompletionSource crashed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<ProbePlan>().Behaviour = async (payload, context, _) =>
        {
            // The handler's own idempotent write (an upsert by business key) commits on its own connection.
            await host.Database.ExecuteAsync($"INSERT INTO test_widgets (name) SELECT '{payload.Name}' WHERE NOT EXISTS (SELECT 1 FROM test_widgets WHERE name = '{payload.Name}')");
            if (context.Attempt == 1)
            {
                await crashed.Task; // The process "dies" here: no outcome is ever recorded for attempt 1.
            }

            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new ProbePayload("idem-1"));

        Task<ExecutionResult> first = host.ExecuteAsync((await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromSeconds(1)))!);
        await host.Get<ProbePlan>().Started("idem-1").WaitAsync(TimeSpan.FromSeconds(10));
        (await JobProbes.EventuallyAsync(async () => await host.CountWidgetsAsync("idem-1") == 1, TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await JobProbes.EventuallyAsync(async () => (await host.Database.QueueStateAsync(id))!.Value.Expired, TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await host.RecoverAsync()).Should().Be(1);
        await host.Database.MakeAvailableNowAsync(id);

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.Succeeded);

        (await host.CountWidgetsAsync("idem-1")).Should().Be(1, "the re-run's write is an idempotent no-op");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));

        // The zombie attempt finally returns: its completion is discarded.
        crashed.SetResult();
        (await first).Should().Be(ExecutionResult.LeaseLost);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        (await host.EventsAsync(id)).Count(e => e == JobEventCodes.JobSucceeded).Should().Be(1);
    }
}