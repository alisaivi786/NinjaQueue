using NinjaQueue.Execution;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// G5 (§16.5, §15.5): fault injection around <c>context.SideEffectAsync</c> — before the call, during it (provider timeout),
/// after remote success but before the ledger completes (a stale worker), and after completion. The ledger state and the
/// <c>OnAmbiguous</c> outcome match §15.5 in every case; a call is issued twice only by a recorded takeover
/// (<c>SideEffectDuplicateRisk</c>), never blindly.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
[Trait("Gate", "G5")]
public sealed class G5SideEffectLedgerTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_completed_step_is_issued_once_and_reused_by_later_attempts(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        host.Plan().FailAfterSideEffectOnAttempts.Add(1);
        JobId id = await host.EnqueueAsync(new P7bExternalPayload("once"));

        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);

        host.Plan().ProviderReceipts.Should().ContainSingle("the step completed on attempt 1 is never issued again");
        host.Plan().ProviderReceipts.Single().Should().Be($"{id.Value:N}:send", "the operation key is stable across attempts");
        host.Plan().Results.Select(r => r.Reused).Should().Equal(false, true);
        (await host.LedgerAsync(id))!.Value.Should().Match<(SideEffectStatus Status, int IssueCount, Guid Owner)>(l => l.Status == SideEffectStatus.Completed && l.IssueCount == 1);
        (await host.EventCountAsync(id, JobEventCodes.SideEffectDuplicateRisk)).Should().Be(0);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Not_issued_is_abandoned_and_issued_again_without_a_duplicate_risk(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        host.Plan().Issue = (_, context, key, _) =>
        {
            if (context.Attempt == 1)
            {
                return Task.FromResult(SideEffectOutcome.NotIssued("connect refused"));
            }

            host.Plan().ProviderReceipts.Enqueue(key);
            return Task.FromResult(SideEffectOutcome.Completed("250 OK"));
        };
        JobId id = await host.EnqueueAsync(new P7bExternalPayload("not-issued"));

        (await host.RunOneAsync(P7b.External)).Should().Be(ExecutionResult.Rescheduled);
        (await host.LedgerAsync(id))!.Value.Status.Should().Be(SideEffectStatus.Abandoned);
        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);

        host.Plan().ProviderReceipts.Should().ContainSingle();
        (await host.LedgerAsync(id))!.Value.Status.Should().Be(SideEffectStatus.Completed);
        (await host.EventCountAsync(id, JobEventCodes.SideEffectDuplicateRisk)).Should().Be(0, "an abandoned call was never issued");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_provider_timeout_is_retried_once_by_a_recorded_takeover_then_marked_unknown(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        host.Plan().Issue = (_, _, key, _) =>
        {
            host.Plan().ProviderReceipts.Enqueue(key);
            return Task.FromResult(SideEffectOutcome.Ambiguous("timeout after DATA"));
        };
        JobId id = await host.EnqueueAsync(new P7bExternalPayload("timeout"));

        (await host.RunOneAsync(P7b.External)).Should().Be(ExecutionResult.Rescheduled, "Retry: the next attempt takes the step over");
        (await host.LedgerAsync(id))!.Value.Status.Should().Be(SideEffectStatus.Ambiguous);
        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);

        host.Plan().ProviderReceipts.Should().HaveCount(2, "MaxIssues = 2: one extra send, then the policy falls back to MarkUnknown");
        (await host.LedgerAsync(id))!.Value.Should().Match<(SideEffectStatus Status, int IssueCount, Guid Owner)>(l => l.Status == SideEffectStatus.Ambiguous && l.IssueCount == 2);
        (await host.EventCountAsync(id, JobEventCodes.SideEffectDuplicateRisk)).Should().Be(1, "the re-issue is recorded");
        (await host.EventCountAsync(id, JobEventCodes.SideEffectUnknown)).Should().Be(1);
        (await host.Database.JobColumnAsync<string>(id, "last_error_code")).Should().Be("SideEffectUnknown", "unknown outcomes surface in admin");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stale_worker_retry_policy_takes_over_once_and_the_late_completion_is_a_recorded_duplicate(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        (JobId id, Task<ExecutionResult> stale, TaskCompletionSource release) = await StaleWorkerAsync(host, null);

        // Worker B (attempt 2) never issues blindly: Retry takes the step over (one CAS) and issues again.
        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);
        host.Plan().ProviderReceipts.Should().HaveCount(2);
        (await host.LedgerAsync(id))!.Value.Should().Match<(SideEffectStatus Status, int IssueCount, Guid Owner)>(l => l.Status == SideEffectStatus.Completed && l.IssueCount == 2);
        Guid owner = (await host.LedgerAsync(id))!.Value.Owner;

        // Worker A completes late: its owner check fails, the newer record stays, the duplicate risk is recorded.
        release.SetResult();
        (await stale).Should().Be(ExecutionResult.LeaseLost);
        (await host.LedgerAsync(id))!.Value.Owner.Should().Be(owner, "the newer owner's record is left in place");
        (await host.EventCountAsync(id, JobEventCodes.SideEffectDuplicateRisk)).Should().Be(2, "takeover + late completion");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stale_worker_mark_unknown_policy_issues_nothing_and_keeps_the_late_truth(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        (JobId id, Task<ExecutionResult> stale, TaskCompletionSource release) = await StaleWorkerAsync(host, SideEffectAmbiguity.MarkUnknown);

        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);
        host.Plan().ProviderReceipts.Should().ContainSingle("MarkUnknown never re-issues");
        (await host.LedgerAsync(id))!.Value.Status.Should().Be(SideEffectStatus.Ambiguous);
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.SideEffectUnknown);

        release.SetResult();
        (await stale).Should().Be(ExecutionResult.LeaseLost);
        (await host.LedgerAsync(id))!.Value.Status.Should().Be(SideEffectStatus.Completed, "the stale worker's call did succeed, and still owns the step");
        (await host.EventCountAsync(id, JobEventCodes.SideEffectDuplicateRisk)).Should().Be(0, "nothing was issued twice");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stale_worker_dead_letter_policy_dead_letters_the_job(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        (JobId id, Task<ExecutionResult> stale, TaskCompletionSource release) = await StaleWorkerAsync(host, SideEffectAmbiguity.DeadLetter);

        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.DeadLettered);
        (await host.Database.JobColumnAsync<string>(id, "last_error_code")).Should().Be("SideEffectAmbiguous");
        host.Plan().ProviderReceipts.Should().ContainSingle();
        release.SetResult();
        await stale;
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stale_worker_with_reconcile_resolves_the_step_without_issuing(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        host.Plan().Reconcile = (key, _) => Task.FromResult(host.Plan().ProviderReceipts.Contains(key)
            ? SideEffectOutcome.Completed("found-by-key")
            : SideEffectOutcome.NotIssued("unknown key"));
        (JobId id, Task<ExecutionResult> stale, TaskCompletionSource release) = await StaleWorkerAsync(host, null);

        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);
        host.Plan().ProviderReceipts.Should().ContainSingle("reconcile found the operation by its key");
        (await host.LedgerAsync(id))!.Value.Should().Match<(SideEffectStatus Status, int IssueCount, Guid Owner)>(l => l.Status == SideEffectStatus.Completed && l.IssueCount == 1);
        release.SetResult();
        await stale;
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_crash_before_the_call_is_treated_as_unknown(DatabaseKind kind)
    {
        // The step was begun (ledger Started) and the worker died before issuing: the next attempt cannot know that, so the
        // policy applies (MarkUnknown here): nothing is issued blindly.
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        host.Plan().SideEffectOptions = new SideEffectOptions { OnAmbiguous = SideEffectAmbiguity.MarkUnknown };
        JobId id = await host.EnqueueAsync(new P7bExternalPayload("crash-before"));
        ClaimedJob first = (await host.ClaimOneAsync(P7b.External))!;
        await host.InScopeAsync(sp => sp.GetRequiredService<IJobSideEffectStore>().BeginSideEffectAsync(first, "send", CancellationToken.None));
        await host.ExpireLeaseAsync(first.Id);
        (await host.RecoverAsync()).Should().Be(1);

        (await host.RunUntilTerminalAsync(id, P7b.External)).Should().Be(JobStatus.Succeeded);
        host.Plan().ProviderReceipts.Should().BeEmpty();
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.SideEffectUnknown);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    /// <summary>
    /// Worker A (attempt 1) issues the call, the provider accepts it, and A stalls before recording the result; its lease
    /// expires and recovery reschedules the job. Returns A's pending execution and the gate that lets A finish.
    /// </summary>
    private static async Task<(JobId Id, Task<ExecutionResult> Stale, TaskCompletionSource Release)> StaleWorkerAsync(Phase4Host host, SideEffectAmbiguity? policy)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Plan().SideEffectOptions = policy is { } p ? new SideEffectOptions { OnAmbiguous = p } : null;
        host.Plan().Issue = async (_, context, key, _) =>
        {
            host.Plan().ProviderReceipts.Enqueue(key);
            if (context.Attempt == 1)
            {
                sent.TrySetResult();
                await release.Task;
            }

            return SideEffectOutcome.Completed($"msg-{context.Attempt}");
        };

        JobId id = await host.EnqueueAsync(new P7bExternalPayload("stale"));
        ClaimedJob first = (await host.ClaimOneAsync(P7b.External))!;
        Task<ExecutionResult> stale = Task.Run(() => host.ExecuteAsync(first));
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.ExpireLeaseAsync(first.Id);
        (await host.RecoverAsync()).Should().Be(1);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        return (id, stale, release);
    }
}