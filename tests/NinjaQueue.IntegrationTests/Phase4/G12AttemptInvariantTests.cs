using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// G12 (§17.2, §14.1): the attempt invariant at the final attempt. <c>attempt</c> never exceeds <c>max_attempts</c>; a running
/// job on its last attempt holds <c>attempt == max_attempts</c> with <c>attempts_left = 0</c> and a lease; a failure, lease loss
/// or timeout on it dead-letters; a snooze or release on it refunds; and the database CHECK refuses a violating write.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
[Trait("Gate", "G12")]
public sealed class G12AttemptInvariantTests(JobsDatabaseFixture fixture)
{
    private const string Standard = "Test.P4.Standard.v1";
    private const string Timeout = "Test.P4.Timeout.v1";
    private const string FencedTimeout = "Test.P4.FencedTimeout.v1";
    private const string Race = "Test.P4.Race.v1";

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Success_on_the_final_attempt_runs_with_attempt_equal_to_max(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed", FailFirstAttempts: 1), new JobOptions { MaxAttempts = 2 });
        (string Leased, int AttemptsLeft, int Attempt, int Max)? whileRunning = null;
        host.Probe.During = async (job, _) =>
        {
            if (host.Probe.Invocations[job] == 2)
            {
                object?[] row = (await host.Database.QueryAsync($"""
                    SELECT CASE WHEN q.lease_token IS NULL THEN 'no' ELSE 'yes' END, q.attempts_left, b.attempt, b.max_attempts
                      FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{job}'
                    """)).Single();
                whileRunning = ((string)row[0]!, Phase4Host.Int(row[1]), Phase4Host.Int(row[2]), Phase4Host.Int(row[3]));
            }
        };

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.Rescheduled);
        await host.WaitUntilAvailableAsync(id);
        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.Succeeded);

        whileRunning.Should().Be(("yes", 0, 2, 2), "the final attempt runs leased with attempt == max_attempts and attempts_left = 0");
        host.Probe.Seen.Select(s => (s.Attempt, s.MaxAttempts)).Should().Equal((1, 2), (2, 2));
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 2));
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Failure_on_the_final_attempt_dead_letters_and_never_reschedules(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("fail"), new JobOptions { MaxAttempts = 2 });

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.Rescheduled);
        (await host.QueueRowAsync(id)).Should().Be(((Guid?)null, 1));
        await host.WaitUntilAvailableAsync(id);
        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.DeadLettered);

        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.Exhausted);
        (await host.QueueRowAsync(id)).Should().BeNull();
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 2));
        (await host.EventsAsync(id)).Should().ContainSingle(e => e == JobEventCodes.JobDeadLettered);
        (await host.ClaimAsync([Standard], 10)).Should().BeEmpty("no claim ever returns an exhausted job");
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_loss_on_the_final_attempt_is_dead_lettered_by_recovery(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed"), new JobOptions { MaxAttempts = 1 });
        ClaimedJob crashed = (await host.ClaimOneAsync(Standard, lease: TimeSpan.FromMilliseconds(100)))!;
        crashed.Attempt.Should().Be(1);
        (await host.QueueRowAsync(id)).Should().Be(((Guid?)crashed.LeaseToken, 0));
        await Task.Delay(400);

        (await host.ClaimAsync([Standard], 10)).Should().BeEmpty("an expired but exhausted lease is never claimable; only recovery may end it");
        (await host.RecoverAsync()).Should().Be(1);

        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.Exhausted);
        (await host.QueueRowAsync(id)).Should().BeNull();
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 1));
        (await host.AttemptRowsAsync(id)).Should().Equal((1, (short)JobAttemptOutcome.LeaseLost, true));
        (await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RecordOutcomeAsync(crashed, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)))
            .Should().Be(OutcomeResult.LeaseLost, "the crashed owner's late outcome is fenced out");
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Timeout_on_the_final_attempt_dead_letters_standard_and_fenced_jobs(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId standard = await host.EnqueueAsync(new P4TimeoutPayload("t"), new JobOptions { MaxAttempts = 1 });
        JobId fenced = await host.EnqueueAsync(new FencedTimeoutPayload("t"), new JobOptions { MaxAttempts = 1 });

        (await host.RunOneAsync(Timeout)).Should().Be(ExecutionResult.DeadLettered);
        (await host.RunOneAsync(FencedTimeout)).Should().Be(ExecutionResult.DeadLettered);

        foreach (JobId id in new[] { standard, fenced })
        {
            (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
            (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.Exhausted);
            (await host.AttemptsAsync(id)).Should().Be((1, 1, 1));
            (await host.AttemptRowsAsync(id)).Should().Equal((1, (short)JobAttemptOutcome.TimedOut, true));
        }

        (await host.WidgetsForAsync(fenced)).Should().Be(0, "the timed-out fenced step rolled back its write");
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancel_during_the_final_attempt_cancels_without_charging(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new RacePayload(1), new JobOptions { MaxAttempts = 1 });
        host.Probe.During = (job, _) => host.Database.ExecuteAsync(
            $"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'g12' WHERE guid_id = '{job}' AND status < 10");

        (await host.RunOneAsync(Race)).Should().Be(ExecutionResult.Cancelled);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Cancelled);
        (await host.WidgetsForAsync(id)).Should().Be(0);
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 1), "a cancelled attempt is refunded");
        (await host.QueueRowAsync(id)).Should().BeNull();
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Snooze_and_release_on_the_final_attempt_refund_it_and_make_the_job_claimable(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId snoozed = await host.EnqueueAsync(new P4Payload("snooze"), new JobOptions { MaxAttempts = 1 });
        JobId released = await host.EnqueueAsync(new RacePayload(1), new JobOptions { MaxAttempts = 1 });

        // Snooze on the only attempt: refunded, unleased, claimable again with one attempt left.
        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.Rescheduled);
        (await host.StatusAsync(snoozed)).Should().Be(JobStatus.Scheduled);
        (await host.QueueRowAsync(snoozed)).Should().Be(((Guid?)null, 1));
        (await host.AttemptsAsync(snoozed)).Should().Be((0, 0, 1));
        (await host.AttemptRowsAsync(snoozed)).Should().Equal((1, (short)JobAttemptOutcome.Snoozed, false));
        await host.WaitUntilAvailableAsync(snoozed);
        ClaimedJob again = (await host.ClaimOneAsync(Standard))!;
        again.Attempt.Should().Be(1, "the refunded attempt is the one used again");
        (await host.QueueRowAsync(snoozed)).Should().Be(((Guid?)again.LeaseToken, 0));
        await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RecordOutcomeAsync(again, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None));
        (await host.StatusAsync(snoozed)).Should().Be(JobStatus.Succeeded);

        // Release (shutdown between fenced steps) on the only attempt: refunded and immediately claimable.
        ClaimedJob job = (await host.ClaimOneAsync(Race))!;
        using (CancellationTokenSource shutdown = new())
        {
            await shutdown.CancelAsync();
            (await host.ExecuteAsync(job, shutdown.Token)).Should().Be(ExecutionResult.Rescheduled);
        }

        (await host.StatusAsync(released)).Should().Be(JobStatus.Enqueued);
        (await host.QueueRowAsync(released)).Should().Be(((Guid?)null, 1));
        (await host.AttemptsAsync(released)).Should().Be((0, 0, 1));
        (await host.EventsAsync(released)).Should().Contain(JobEventCodes.JobReleased);
        (await host.RunOneAsync(Race)).Should().Be(ExecutionResult.Succeeded);
        (await host.AttemptsAsync(released)).Should().Be((1, 1, 1));
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task The_database_refuses_writes_that_break_the_attempt_invariant(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed"), new JobOptions { MaxAttempts = 1 });
        ClaimedJob job = (await host.ClaimOneAsync(Standard))!;
        (await host.QueueRowAsync(id)).Should().Be(((Guid?)job.LeaseToken, 0));

        // Clearing the lease of an exhausted row would make it claimable: the CHECK refuses it.
        await FluentActions.Awaiting(() => host.Database.ExecuteAsync(
                $"UPDATE ninjaqueue.job_queue SET lease_token = NULL, lease_owner = NULL, lease_expires_at_utc = NULL WHERE job_id = {job.Id}"))
            .Should().ThrowAsync<DbException>().WithMessage("*ck_job_queue_claimable_has_attempts*");
        await FluentActions.Awaiting(() => host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET attempt = max_attempts + 1 WHERE id = {job.Id}"))
            .Should().ThrowAsync<DbException>().WithMessage("*ck_background_jobs_attempt*");
        await FluentActions.Awaiting(() => host.Database.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET attempts_left = -1 WHERE job_id = {job.Id}"))
            .Should().ThrowAsync<DbException>().WithMessage("*ck_job_queue_attempts_left*");

        (await host.QueueRowAsync(id)).Should().Be(((Guid?)job.LeaseToken, 0), "every refused write left the row unchanged");
        (await host.ClaimAsync([Standard], 10)).Should().BeEmpty();
        await JobInvariants.AssertHoldAsync(host.Database);
    }
}