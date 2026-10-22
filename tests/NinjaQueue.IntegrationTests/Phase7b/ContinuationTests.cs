using NinjaQueue.Execution;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// Continuations (§15.6, Release 2): Awaiting with no queue row, release only on parent success (or any completion for
/// <c>OnParentCompletion</c>), cascade on failure and cancel, the create-vs-complete race closed by the parent row lock,
/// and unique keys held while Awaiting.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
public sealed class ContinuationTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_child_waits_and_is_released_only_when_the_parent_succeeds(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId child = await host.EnqueueAsync(new P7bPayload("child"), new JobOptions { ContinueAfter = parent });
        JobId grandchild = await host.EnqueueAsync(new P7bPayload("grandchild"), new JobOptions { ContinueAfter = child });

        (await host.StatusAsync(child)).Should().Be(JobStatus.Awaiting);
        (await host.CountQueueAsync(child)).Should().Be(0, "an Awaiting child has no queue row and cannot be claimed");
        (await host.ClaimOneAsync(P7b.Step)).Should().BeNull();
        await P7b.AssertInvariantsAsync(host.Database);

        (await host.RunOneAsync(P7b.P4Standard)).Should().Be(ExecutionResult.Succeeded);
        (await host.StatusAsync(child)).Should().Be(JobStatus.Enqueued);
        (await host.EventsAsync(child)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.ContinuationReleased);
        (await host.StatusAsync(grandchild)).Should().Be(JobStatus.Awaiting, "only the direct children are released");

        (await host.RunOneAsync(P7b.Step)).Should().Be(ExecutionResult.Succeeded);
        (await host.RunOneAsync(P7b.Step)).Should().Be(ExecutionResult.Succeeded);
        (await host.StatusAsync(grandchild)).Should().Be(JobStatus.Succeeded);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_dead_lettered_parent_cancels_success_children_and_releases_completion_children(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new P4Payload("permanent"));
        JobId onSuccess = await host.EnqueueAsync(new P7bPayload("s"), new JobOptions { ContinueAfter = parent });
        JobId onCompletion = await host.EnqueueAsync(new P7bPayload("c"), new JobOptions { ContinueAfter = parent, ContinuationPolicy = ContinuationPolicy.OnParentCompletion });
        JobId grandOnSuccess = await host.EnqueueAsync(new P7bPayload("gs"), new JobOptions { ContinueAfter = onSuccess });
        JobId grandOnCompletion = await host.EnqueueAsync(new P7bPayload("gc"), new JobOptions { ContinueAfter = onSuccess, ContinuationPolicy = ContinuationPolicy.OnParentCompletion });

        (await host.RunOneAsync(P7b.P4Standard)).Should().Be(ExecutionResult.DeadLettered);

        (await host.StatusAsync(onSuccess)).Should().Be(JobStatus.Cancelled);
        (await host.TerminalReasonAsync(onSuccess)).Should().Be((short)JobTerminalReason.ParentFailed);
        (await host.EventsAsync(onSuccess)).Should().EndWith(JobEventCodes.ContinuationCancelled);
        (await host.StatusAsync(onCompletion)).Should().Be(JobStatus.Enqueued);
        (await host.StatusAsync(grandOnSuccess)).Should().Be(JobStatus.Cancelled, "a cancel cascades down the chain");
        (await host.TerminalReasonAsync(grandOnSuccess)).Should().Be((short)JobTerminalReason.ParentCancelled);
        (await host.StatusAsync(grandOnCompletion)).Should().Be(JobStatus.Enqueued);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_child_of_a_terminal_parent_is_decided_at_once_and_tenants_cannot_be_crossed(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId succeeded = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId failed = await host.EnqueueAsync(new P4Payload("permanent"));
        await host.RunOneAsync(P7b.P4Standard);
        await host.RunOneAsync(P7b.P4Standard);

        JobId released = await host.EnqueueAsync(new P7bPayload("r"), new JobOptions { ContinueAfter = succeeded });
        JobId cancelled = await host.EnqueueAsync(new P7bPayload("x"), new JobOptions { ContinueAfter = failed, UniqueKey = "u:child" });
        (await host.StatusAsync(released)).Should().Be(JobStatus.Enqueued);
        (await host.StatusAsync(cancelled)).Should().Be(JobStatus.Cancelled);
        (await host.TerminalReasonAsync(cancelled)).Should().Be((short)JobTerminalReason.ParentFailed);
        (await host.EnqueueAsync(new P7bPayload("y"), new JobOptions { UniqueKey = "u:child" })).Should().NotBe(cancelled, "a cancelled child holds no reservation");

        JobId tenantParent = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "t"), new JobOptions { TenantId = P7b.TenantA });
        await FluentActions.Awaiting(() => host.EnqueueAsync(new P7bTenantPayload(P7b.TenantB, "t"), new JobOptions { TenantId = P7b.TenantB, ContinueAfter = tenantParent }))
            .Should().ThrowAsync<ArgumentException>("a parent of another tenant is reported as not found");
        await FluentActions.Awaiting(() => host.EnqueueAsync(new P7bPayload("sys"), new JobOptions { ContinueAfter = tenantParent }))
            .Should().ThrowAsync<ArgumentException>();
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancelling_a_child_leaves_the_parent_and_cancelling_a_parent_cascades(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new P4Payload("succeed"), new JobOptions { RunAt = DateTimeOffset.UtcNow.AddHours(1) });
        JobId first = await host.EnqueueAsync(new P7bPayload("a"), new JobOptions { ContinueAfter = parent });
        JobId second = await host.EnqueueAsync(new P7bPayload("b"), new JobOptions { ContinueAfter = parent });

        (await host.CancelAsync(first)).Should().Be(CancelResult.Cancelled);
        (await host.StatusAsync(parent)).Should().Be(JobStatus.Scheduled);
        (await host.StatusAsync(second)).Should().Be(JobStatus.Awaiting);

        (await host.CancelAsync(parent)).Should().Be(CancelResult.Cancelled);
        (await host.StatusAsync(second)).Should().Be(JobStatus.Cancelled);
        (await host.TerminalReasonAsync(second)).Should().Be((short)JobTerminalReason.ParentCancelled);
        (await host.TerminalReasonAsync(first)).Should().Be((short)JobTerminalReason.AdminCancelled);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Awaiting_children_keep_their_unique_key_through_creation_and_release(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new P4Payload("succeed"));

        JobId[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            host.EnqueueAsync(new P7bPayload($"u{i}"), new JobOptions { ContinueAfter = parent, UniqueKey = "digest:42" })));
        created.Distinct().Should().ContainSingle("concurrent creation of children with one unique key yields exactly one job");
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE unique_key = 'digest:42'")).Should().Be(1);

        (await host.RunOneAsync(P7b.P4Standard)).Should().Be(ExecutionResult.Succeeded);
        (await host.StatusAsync(created[0])).Should().Be(JobStatus.Enqueued);
        (await host.EnqueueAsync(new P7bPayload("again"), new JobOptions { UniqueKey = "digest:42" })).Should().Be(created[0], "the released child still holds the key");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Parent_outcome_racing_child_creation_never_strands_or_wrongly_releases_a_child(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        for (int i = 0; i < 40; i++)
        {
            bool fails = i % 2 == 1;
            JobId parent = await host.EnqueueAsync(new P4Payload(fails ? "permanent" : "succeed"));
            ClaimedJob claimed = (await host.ClaimOneAsync(P7b.P4Standard))!;
            Task<ExecutionResult> outcome = host.ExecuteAsync(claimed);
            Task<JobId> child = host.EnqueueAsync(new P7bPayload($"c{i}"), new JobOptions { ContinueAfter = parent });
            await Task.WhenAll(outcome, child);
            JobId childId = await child;

            JobStatus childStatus = await host.StatusAsync(childId);
            childStatus.Should().Be(fails ? JobStatus.Cancelled : JobStatus.Enqueued, $"iteration {i}: the child follows the parent's final outcome");
            (await host.CountQueueAsync(childId)).Should().Be(fails ? 0 : 1);
            if (!fails)
            {
                await host.RunOneAsync(P7b.Step);
            }
        }

        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_fenced_parent_releases_its_children_in_its_completion_transaction(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobId parent = await host.EnqueueAsync(new WidgetPayload(P7b.TenantA, "fenced"), new JobOptions { TenantId = P7b.TenantA });
        JobId child = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "after-fenced"), new JobOptions { TenantId = P7b.TenantA, ContinueAfter = parent });

        (await host.RunUntilTerminalAsync(parent, "Test.Widgets.Create.v1")).Should().Be(JobStatus.Succeeded);
        (await host.StatusAsync(child)).Should().Be(JobStatus.Enqueued);
        (await host.EventsAsync(child)).Should().Contain(JobEventCodes.ContinuationReleased);
        await P7b.AssertInvariantsAsync(host.Database);
    }
}