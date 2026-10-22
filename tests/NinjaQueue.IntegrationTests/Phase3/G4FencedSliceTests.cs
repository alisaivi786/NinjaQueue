using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase3;

/// <summary>
/// G4 (§16.4, §17.3, §17.12): the shared-transaction proof. Business write + Stage, the real claim, fenced steps whose handler
/// calls a provider that opens its own unit of work (which must join), and the finalization/completion, with a failure
/// injected at every boundary. Each step commits completely or rolls back completely, verified from a second connection.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "G4")]
public sealed class G4FencedSliceTests(JobsDatabaseFixture fixture)
{
    private const string JobType = "Test.Widgets.Create.v1";
    private static readonly Guid Tenant = Guid.Parse("cccccccc-0000-0000-0000-00000000000c");

    private Task<JobsHost> HostAsync(DatabaseKind kind) => JobsHost.StartAsync(fixture, kind, b => b.AddJobHandler<CreateWidgetsHandler>());

    private static Task<JobId> StageAsync(JobsHost host, string name)
        => host.StageInTransactionAsync(new WidgetPayload(Tenant, name), new JobOptions { TenantId = Tenant });

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Happy_path_commits_each_step_and_the_completion_together(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "ok");

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.Succeeded);

        (await host.CountWidgetsAsync("ok-")).Should().Be(2, "both steps' provider writes committed");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        (await host.CountQueueAsync(id)).Should().Be(0);
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobSucceeded);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3));
        (await host.Database.ScalarAsync<int>($"SELECT progress_processed FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'")).Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_after_the_providers_nested_write_rolls_the_step_back(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "nested");
        host.Get<FaultPlan>().ThrowAfterWriteAtCheckpoint = "second";

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.Rescheduled);

        // Step 1 committed with its checkpoint; step 2's provider write rolled back with the step.
        (await host.CountWidgetsAsync("nested-start")).Should().Be(1);
        (await host.CountWidgetsAsync("nested-second")).Should().Be(0);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        (await host.Database.ScalarAsync<string>($"SELECT {(host.Database.IsPostgres ? "checkpoint" : "[checkpoint]")} FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'")).Should().Be("second");

        host.Get<FaultPlan>().ThrowAfterWriteAtCheckpoint = null;
        await WaitUntilAvailableAsync(host, id);
        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.Succeeded);
        (await host.CountWidgetsAsync("nested-start")).Should().Be(1, "the retry resumed from the committed checkpoint");
        (await host.CountWidgetsAsync("nested-second")).Should().Be(1);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_after_finalization_before_commit_rolls_everything_back(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "precommit");
        ClaimedJob job = (await host.ClaimOneAsync(JobType))!;

        FencedStepOutcome outcome = await host.InScopeAsync(async sp =>
        {
            FencedStepTestHooks.BeforeCommit.Value = () => throw new InvalidOperationException("Injected failure after finalization, before COMMIT.");
            try
            {
                return await sp.GetRequiredService<IJobStore>().RunFencedStepAsync(job, new FencedStepSettings(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20), true),
                    async (checkpoint, ct) =>
                    {
                        await sp.GetRequiredService<WidgetProvider>().AddAsync("precommit-x", ct);
                        return new FencedStepResult(true, null, null, null);
                    }, CancellationToken.None);
            }
            finally
            {
                FencedStepTestHooks.BeforeCommit.Value = null;
            }
        }).ContinueWith(t => t.IsFaulted ? new FencedStepOutcome(FencedStepStatus.Failed, Error: t.Exception) : t.Result, TaskScheduler.Default);

        outcome.Status.Should().Be(FencedStepStatus.Failed);
        (await host.CountWidgetsAsync("precommit")).Should().Be(0);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "the finalization rolled back with the step");
        (await host.CountQueueAsync(id)).Should().Be(1);
        (await host.EventsAsync(id)).Should().NotContain(JobEventCodes.JobSucceeded);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_token_invalidated_before_the_fence_runs_nothing(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "stolen");
        ClaimedJob job = (await host.ClaimOneAsync(JobType))!;
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET lease_token = '{Guid.NewGuid()}' WHERE job_id = {job.Id}");

        using JobSignals signals = new(CancellationToken.None);
        (await host.Get<JobExecutor>().ExecuteAsync(job, signals)).Should().Be(ExecutionResult.LeaseLost);

        host.Get<FaultPlan>().Calls.Should().Be(0, "the handler never runs without the lease");
        (await host.CountWidgetsAsync("stolen")).Should().Be(0);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "nothing is written for a lease this worker does not hold");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancel_committed_between_fence_and_finalization_wins(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "cancel");
        host.Get<FaultPlan>().DuringStep = () => host.Database.ExecuteAsync(
            $"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'test' WHERE guid_id = '{id.Value}' AND status < 10");

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.Cancelled);

        (await host.CountWidgetsAsync("cancel")).Should().Be(0, "the step's writes rolled back when the cancel won");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Cancelled);
        (await host.CountQueueAsync(id)).Should().Be(0);
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.JobCancelled).And.NotContain(JobEventCodes.JobSucceeded);
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 3), "a cancelled attempt is not charged");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Handler_committing_the_engine_transaction_is_refused_and_commits_nothing(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "guard");
        host.Get<FaultPlan>().CommitEngineTransaction = true;

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.DeadLettered, "a contract violation is permanent");

        (await host.CountWidgetsAsync("guard")).Should().Be(0);
        (await host.Database.ScalarAsync<string>($"""
            SELECT a.exception_type FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE b.guid_id = '{id.Value}'
            """)).Should().Be(typeof(FencedContractViolationException).FullName);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Handler_opening_a_second_unit_of_work_is_refused(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await StageAsync(host, "second");
        host.Get<FaultPlan>().OpenSecondUnitOfWork = true;

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.DeadLettered);

        (await host.CountWidgetsAsync("second")).Should().Be(0, "neither the joined write nor the escaped one committed");
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Uncommitted_step_writes_are_invisible_to_a_second_connection(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        await StageAsync(host, "invisible");
        int seenDuringStep = -1;
        host.Get<FaultPlan>().DuringStep = async () => seenDuringStep = await host.CountWidgetsAsync("invisible");

        (await host.RunOneAsync(JobType)).Should().Be(ExecutionResult.Succeeded);

        seenDuringStep.Should().Be(1, "only step 1's write is committed while step 2 runs; step 2's own write is not yet visible");
        (await host.CountWidgetsAsync("invisible")).Should().Be(2);
    }

    private static async Task WaitUntilAvailableAsync(JobsHost host, JobId id)
    {
        for (int i = 0; i < 50; i++)
        {
            string ready = host.Database.IsPostgres ? "available_at_utc <= now()" : "available_at_utc <= SYSDATETIMEOFFSET()";
            if (await host.Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{id.Value}' AND {ready}") == 1)
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}