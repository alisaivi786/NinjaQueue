using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// G4 regression against the hardened engine (§17.12, Phase 4 exit): the G4 scenarios of the Phase 3 spike, driven through the
/// full worker path (<c>NinjaQueueWorkerHost</c>: claim loop, executor from DI, retries) instead of the spike helpers. The
/// Phase 3 spike tests stay unchanged. Each step commits completely or rolls back completely, verified from a second
/// connection, on PostgreSQL 16 and SQL Server 2022 RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
[Trait("Gate", "G4")]
public sealed class G4RegressionTests(JobsDatabaseFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");

    private static Task<JobId> StageAsync(Phase4Host host, string name, bool failBeforeCommit = false)
        => host.InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>().UseTransactionAsync(async (session, ct) =>
        {
            session.DbContext.Widgets.Add(new Widget { Name = $"{name}-business" });
            await session.DbContext.SaveChangesAsync(ct);
            JobId id = await sp.GetRequiredService<IJobEnqueuer>().StageAsync(new WidgetPayload(Tenant, name), new JobOptions { TenantId = Tenant }, ct);
            return failBeforeCommit ? throw new InvalidOperationException("Injected failure after Stage.") : id;
        }));

    /// <summary>Runs a fresh worker host (a stopped NinjaQueueWorkerHost cannot restart) on the database until the job is terminal.</summary>
    private static async Task<JobStatus> RunToTerminalAsync(Phase4Host host, JobId id, Action<FaultPlan>? faults = null)
    {
        await using Phase4Host worker = Phase4Host.Build(host.Database);
        faults?.Invoke(worker.Faults);
        await worker.StartWorkersAsync();
        await ConcurrencySuiteTests.WaitAsync(async () => (await host.StatusAsync(id)).IsTerminal(), TimeSpan.FromSeconds(60));
        await worker.StopWorkersAsync();
        return await host.StatusAsync(id);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Happy_path_commits_business_write_stage_steps_and_completion(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await StageAsync(host, "ok");

        (await RunToTerminalAsync(host, id)).Should().Be(JobStatus.Succeeded);

        (await host.CountWidgetsAsync("ok-business")).Should().Be(1);
        (await host.CountWidgetsAsync("ok-start")).Should().Be(1);
        (await host.CountWidgetsAsync("ok-second")).Should().Be(1);
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobSucceeded);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3));
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_after_Stage_rolls_back_the_business_write_and_the_job(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        await FluentActions.Awaiting(() => StageAsync(host, "staged", failBeforeCommit: true)).Should().ThrowAsync<InvalidOperationException>();

        (await host.CountWidgetsAsync("staged")).Should().Be(0);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(0);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue")).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_after_the_providers_nested_write_rolls_back_each_attempt_and_resumes_from_the_checkpoint(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await StageAsync(host, "nested");
        (await RunToTerminalAsync(host, id, f => f.ThrowAfterWriteAtCheckpoint = "second")).Should().Be(JobStatus.DeadLettered, "the step fails on every attempt");

        (await host.CountWidgetsAsync("nested-start")).Should().Be(1, "step 1 committed once with its checkpoint; retries resumed from it");
        (await host.CountWidgetsAsync("nested-second")).Should().Be(0, "every attempt of step 2 rolled back its provider write");
        (await host.CheckpointAsync(id)).Should().Be("second");
        (await host.AttemptsAsync(id)).Should().Be((3, 3, 3));
        (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.Exhausted);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_after_finalization_before_commit_rolls_the_step_back_and_the_retry_completes(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await StageAsync(host, "precommit");
        int commits = 0;

        // Set on this flow before the workers start, so their loops inherit it. Throws at the first step's pre-commit point.
        FencedStepTestHooks.BeforeCommit.Value = () => Interlocked.Increment(ref commits) == 1
            ? throw new InjectedCommitFailure()
            : Task.CompletedTask;
        try
        {
            (await RunToTerminalAsync(host, id)).Should().Be(JobStatus.Succeeded);
        }
        finally
        {
            FencedStepTestHooks.BeforeCommit.Value = null;
        }

        commits.Should().BeGreaterThanOrEqualTo(3, "the failed commit, then both steps of the retry");
        (await host.CountWidgetsAsync("precommit-start")).Should().Be(1, "the first attempt's finalized step rolled back completely");
        (await host.CountWidgetsAsync("precommit-second")).Should().Be(1);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));
        (await host.EventsAsync(id)).Should().ContainInOrder(JobEventCodes.JobClaimed, JobEventCodes.JobRetryScheduled, JobEventCodes.JobClaimed, JobEventCodes.JobSucceeded);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_token_invalidated_before_the_next_fence_runs_nothing_and_recovery_resumes(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        bool stolen = false;
        for (int i = 0; i < 5 && !stolen; i++)
        {
            JobId id = await StageAsync(host, $"stolen{i}");
            Task<int> steal = Task.FromResult(0);
            int calls;
            await using (Phase4Host worker = Phase4Host.Build(host.Database))
            {
                FaultPlan faults = worker.Faults;
                faults.DuringStep = () =>
                {
                    if (faults.Calls == 1)
                    {
                        // Queued behind step 1's row lock: takes the lease (and expires it) the moment step 1 commits,
                        // before the worker's step 2 fence.
                        using (ExecutionContext.SuppressFlow())
                        {
                            steal = Task.Run(() => host.Database.ExecuteAsync($"""
                                UPDATE ninjaqueue.job_queue SET lease_token = '{Guid.NewGuid()}', lease_expires_at_utc = {(host.Database.IsPostgres ? "now() - interval '1 minute'" : "DATEADD(minute, -1, SYSDATETIMEOFFSET())")}
                                 WHERE job_id = (SELECT id FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}')
                                """));
                        }
                    }

                    return Task.Delay(50);
                };

                await worker.StartWorkersAsync();
                await ConcurrencySuiteTests.WaitAsync(async () => faults.Calls >= 1 && steal.IsCompleted && await host.CountWidgetsAsync($"stolen{i}-start") == 1, TimeSpan.FromSeconds(30));
                await Task.Delay(500);
                await worker.StopWorkersAsync();
                calls = faults.Calls;
            }

            if (await steal == 1 && calls == 1)
            {
                stolen = true;
                (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "nothing is written by a worker whose fence matched 0 rows");
                (await host.CountWidgetsAsync($"stolen{i}-second")).Should().Be(0, "step 2 never ran without the lease");
                (await host.RecoverAsync()).Should().Be(1);
                (await RunToTerminalAsync(host, id)).Should().Be(JobStatus.Succeeded);
                (await host.CountWidgetsAsync($"stolen{i}-start")).Should().Be(1, "the retry resumed from the committed checkpoint");
                (await host.CountWidgetsAsync($"stolen{i}-second")).Should().Be(1);
                (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));
            }
            else
            {
                (await RunToTerminalAsync(host, id)).Should().Be(JobStatus.Succeeded, "the take-over lost the race and found no row");
            }
        }

        stolen.Should().BeTrue("the lease-loss path was exercised");
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancel_committed_between_fence_and_finalization_wins(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await StageAsync(host, "cancel");
        (await RunToTerminalAsync(host, id, f => f.DuringStep = () => host.Database.ExecuteAsync(
            $"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'g4' WHERE guid_id = '{id.Value}' AND status < 10")))
            .Should().Be(JobStatus.Cancelled);

        (await host.CountWidgetsAsync("cancel-start")).Should().Be(0);
        (await host.CountWidgetsAsync("cancel-business")).Should().Be(1, "the enqueuing transaction's own write is unaffected");
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.JobCancelled).And.NotContain(JobEventCodes.JobSucceeded);
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 3));
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Guard_violations_commit_nothing_and_dead_letter(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId commit = await StageAsync(host, "guardcommit");
        (await RunToTerminalAsync(host, commit, f => f.CommitEngineTransaction = true)).Should().Be(JobStatus.DeadLettered);

        JobId second = await StageAsync(host, "guardsecond");
        (await RunToTerminalAsync(host, second, f => f.OpenSecondUnitOfWork = true)).Should().Be(JobStatus.DeadLettered);

        (await host.CountWidgetsAsync("guardcommit-start")).Should().Be(0);
        (await host.CountWidgetsAsync("guardsecond-start")).Should().Be(0, "the joined write rolled back");
        (await host.CountWidgetsAsync("guardsecond-escaped")).Should().Be(0, "the escaped write was refused");
        (await host.Database.ScalarAsync<int>($"""
            SELECT count(*) FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id
             WHERE b.guid_id IN ('{commit.Value}', '{second.Value}') AND a.exception_type = '{typeof(FencedContractViolationException).FullName}'
            """)).Should().Be(2);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Uncommitted_step_writes_are_invisible_to_a_second_connection(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await StageAsync(host, "invisible");
        List<int> seen = [];
        string readPast = kind == DatabaseKind.SqlServerRcsiOff ? " WITH (READPAST)" : "";
        (await RunToTerminalAsync(host, id, f => f.DuringStep = async () =>
            seen.Add(await host.Database.ScalarAsync<int>($"SELECT count(*) FROM test_widgets{readPast} WHERE name LIKE 'invisible-%' AND name <> 'invisible-business'"))))
            .Should().Be(JobStatus.Succeeded);

        seen.Should().Equal([0, 1], "during each step only the earlier steps' writes are visible");
        (await host.CountWidgetsAsync("invisible-")).Should().Be(3);
    }

#pragma warning disable CA1032, RCS1194 // A test-only database failure: only the constructor used here.
    private sealed class InjectedCommitFailure() : DbException("Injected failure after finalization, before COMMIT.");
#pragma warning restore CA1032, RCS1194
}