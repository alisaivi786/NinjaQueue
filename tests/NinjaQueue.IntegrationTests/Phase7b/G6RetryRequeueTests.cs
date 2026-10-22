using NinjaQueue.Admin;
using NinjaQueue.Execution;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// G6 (§16.5, §15.11): admin Retry versus Requeue over the state table — dead-lettered, cancelled, succeeded, an exhausted
/// persisted budget, unique-key conflicts, restored and conflicting continuations, and concurrent admin actions — with the
/// invariant checker after every scenario and the actor and reason in <c>job_events</c>.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
[Trait("Gate", "G6")]
public sealed class G6RetryRequeueTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Retry_gives_a_dead_lettered_job_exactly_one_more_attempt_with_history_kept(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed", FailFirstAttempts: 3));
        (await host.RunUntilTerminalAsync(id, P7b.P4Standard)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(id)).Should().Be((3, 3, 3));

        AdminRunResult retry = await host.AdminAsync(a => a.RetryAsync(id, P7b.Actor, "smtp relay fixed"));
        retry.Outcome.Should().Be(AdminRunOutcome.Applied);
        retry.Status.Should().Be(JobStatus.Enqueued);
        (await host.AttemptsAsync(id)).Should().Be((3, 3, 4), "max_attempts = attempt + 1; attempt keeps counting");
        (await host.QueueRowAsync(id)).Should().Be(((Guid?)null, 1));
        object?[] evt = (await host.Database.QueryAsync($"""
            SELECT e.actor_id, e.reason, e.data FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id
             WHERE b.guid_id = '{id.Value}' AND e.event_code = '{JobEventCodes.JobRequeued}'
            """)).Single();
        evt[0].Should().Be(P7b.Actor);
        evt[1].Should().Be("smtp relay fixed");
        ((string)evt[2]!).Should().Contain("\"mode\":\"retry\"");
        await P7b.AssertInvariantsAsync(host.Database);

        (await host.RunOneAsync(P7b.P4Standard)).Should().Be(ExecutionResult.Succeeded);
        (await host.AttemptsAsync(id)).Should().Be((4, 4, 4));
        (await host.AdminAsync(a => a.RetryAsync(id, P7b.Actor, "again"))).Outcome.Should().Be(AdminRunOutcome.NotEligible, "succeeded jobs are never re-run");
        host.Get<RecordingAuditSink>().Entries.Should().ContainSingle(e => e.Action == "Retry" && e.JobId == id);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Requeue_gives_a_cancelled_or_dead_lettered_job_a_fresh_budget_and_clears_the_cancel(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId cancelled = await host.EnqueueAsync(new P4Payload("succeed"));
        (await host.CancelAsync(cancelled)).Should().Be(CancelResult.Cancelled);
        (await host.AdminAsync(a => a.RetryAsync(cancelled, P7b.Actor, "retry"))).Outcome.Should().Be(AdminRunOutcome.NotEligible, "Retry is for dead letters only");

        DateTimeOffset later = DateTimeOffset.UtcNow.AddHours(2);
        AdminRunResult requeue = await host.AdminAsync(a => a.RequeueAsync(cancelled, P7b.Actor, "customer asked", later));
        requeue.Outcome.Should().Be(AdminRunOutcome.Applied);
        requeue.Status.Should().Be(JobStatus.Scheduled);
        (await host.AttemptsAsync(cancelled)).Should().Be((0, 0, 3), "max_attempts = attempt + the type default (3)");
        (await host.Database.IsNullAsync(cancelled, "cancel_requested_at_utc")).Should().BeTrue();
        (await host.Database.IsNullAsync(cancelled, "terminal_reason_code")).Should().BeTrue();
        (await host.ClaimOneAsync(P7b.P4Standard)).Should().BeNull("it runs at the requested time");

        JobId dead = await host.EnqueueAsync(new P4Payload("permanent"));
        await host.RunOneAsync(P7b.P4Standard);
        (await host.AdminAsync(a => a.RequeueAsync(dead, P7b.Actor, "requeue"))).Outcome.Should().Be(AdminRunOutcome.Applied);
        (await host.AttemptsAsync(dead)).Should().Be((1, 1, 4));
        (await host.EventsAsync(dead)).Should().EndWith(JobEventCodes.JobRequeued);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_held_unique_key_is_a_conflict_with_the_holder_and_changes_nothing(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId dead = await host.EnqueueAsync(new P4Payload("permanent"), new JobOptions { UniqueKey = "provision:7" });
        await host.RunOneAsync(P7b.P4Standard);
        JobId holder = await host.EnqueueAsync(new P7bPayload("holder"), new JobOptions { UniqueKey = "provision:7" });

        AdminRunResult retry = await host.AdminAsync(a => a.RetryAsync(dead, P7b.Actor, "retry"));
        retry.Outcome.Should().Be(AdminRunOutcome.Conflict);
        retry.HolderJobId.Should().Be(holder);
        (await host.StatusAsync(dead)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(dead)).Should().Be((1, 1, 3));
        (await host.EventsAsync(dead)).Should().NotContain(JobEventCodes.JobRequeued);

        (await host.RunOneAsync(P7b.Step)).Should().Be(ExecutionResult.Succeeded);
        (await host.AdminAsync(a => a.RetryAsync(dead, P7b.Actor, "retry"))).Outcome.Should().Be(AdminRunOutcome.Applied, "the key is free again");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Retry_restores_children_cancelled_for_the_parent_and_reports_conflicting_ones(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new P4Payload("succeed", FailFirstAttempts: 3));
        JobId plain = await host.EnqueueAsync(new P7bPayload("plain"), new JobOptions { ContinueAfter = parent });
        JobId keyed = await host.EnqueueAsync(new P7bPayload("keyed"), new JobOptions { ContinueAfter = parent, UniqueKey = "child:1" });
        JobId grand = await host.EnqueueAsync(new P7bPayload("grand"), new JobOptions { ContinueAfter = plain });
        (await host.RunUntilTerminalAsync(parent, P7b.P4Standard)).Should().Be(JobStatus.DeadLettered);
        (await host.StatusAsync(grand)).Should().Be(JobStatus.Cancelled);
        JobId squatter = await host.EnqueueAsync(new P7bPayload("squatter"), new JobOptions { UniqueKey = "child:1", RunAt = DateTimeOffset.UtcNow.AddHours(1) });

        AdminRunResult retry = await host.AdminAsync(a => a.RetryAsync(parent, P7b.Actor, "retry with children"));
        retry.Should().Be(new AdminRunResult(AdminRunOutcome.Applied, JobStatus.Enqueued, null, 2, 1, JobLimits.SystemTenantScope));
        (await host.StatusAsync(plain)).Should().Be(JobStatus.Awaiting);
        (await host.StatusAsync(grand)).Should().Be(JobStatus.Awaiting, "restoration follows the chain");
        (await host.StatusAsync(keyed)).Should().Be(JobStatus.Cancelled);
        (await host.EventsAsync(keyed)).Should().EndWith(JobEventCodes.ContinuationRestoreConflict);
        (await host.StatusAsync(squatter)).Should().Be(JobStatus.Scheduled);
        await P7b.AssertInvariantsAsync(host.Database);

        (await host.RunOneAsync(P7b.P4Standard)).Should().Be(ExecutionResult.Succeeded);
        (await host.StatusAsync(plain)).Should().Be(JobStatus.Enqueued);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Concurrent_admin_actions_apply_once_and_keep_every_invariant(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        for (int i = 0; i < 6; i++)
        {
            JobId id = await host.EnqueueAsync(new P4Payload("permanent"), new JobOptions { UniqueKey = $"g6:{i}" });
            await host.RunOneAsync(P7b.P4Standard);
            (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);

            // Two admins retrying at once: exactly one applies.
            AdminRunResult[] both = await Task.WhenAll(
                host.AdminAsync(a => a.RetryAsync(id, P7b.Actor, "admin 1")), host.AdminAsync(a => a.RetryAsync(id, Guid.NewGuid(), "admin 2")));
            both.Count(r => r.Outcome == AdminRunOutcome.Applied).Should().Be(1);
            both.Single(r => r.Outcome != AdminRunOutcome.Applied).Outcome.Should().Be(AdminRunOutcome.NotEligible);

            // Requeue racing cancel on the now-queued job: whichever order, the result is consistent.
            await Task.WhenAll(host.CancelAsync(id), host.AdminAsync(a => a.RequeueAsync(id, P7b.Actor, "race")));
            await P7b.AssertInvariantsAsync(host.Database);

            // Requeue after the cancel leaves it queued: run it to its end so the next iteration claims only its own job.
            (await host.RunUntilTerminalAsync(id, P7b.P4Standard)).IsTerminal().Should().BeTrue();
        }

        // Persisted ceiling: one more attempt would exceed 1000.
        JobId capped = await host.EnqueueAsync(new P4Payload("permanent"));
        await host.RunOneAsync(P7b.P4Standard);
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET max_attempts = 1000, attempt = 1000 WHERE guid_id = '{capped.Value}'");
        (await host.AdminAsync(a => a.RetryAsync(capped, P7b.Actor, "too many"))).Outcome.Should().Be(AdminRunOutcome.AttemptLimitReached);
        (await host.AdminAsync(a => a.RequeueAsync(capped, P7b.Actor, "too many"))).Outcome.Should().Be(AdminRunOutcome.AttemptLimitReached);
        await P7b.AssertInvariantsAsync(host.Database, JobInvariants.AttemptEqualsChargedRows);
    }
}