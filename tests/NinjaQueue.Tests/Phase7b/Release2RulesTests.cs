using NinjaQueue.Admin;
using NinjaQueue.Enqueue;
using NinjaQueue.Execution;
using NinjaQueue.Registry;

namespace NinjaQueue.Tests.Phase7b;

/// <summary>The pure Release 2 state rules (§14.5, §15.5, §15.6, §15.11, §17.9) over their full input tables.</summary>
public sealed class Release2RulesTests
{
    public static TheoryData<JobStatus> AllStatuses => [.. Enum.GetValues<JobStatus>()];

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Continuation_decision_covers_every_parent_state(JobStatus parent)
    {
        ContinuationDecision onSuccess = ContinuationRules.Decide(parent, ContinuationPolicy.OnParentSuccess);
        ContinuationDecision onCompletion = ContinuationRules.Decide(parent, ContinuationPolicy.OnParentCompletion);
        if (!parent.IsTerminal())
        {
            onSuccess.Should().Be(ContinuationDecision.Await);
            onCompletion.Should().Be(ContinuationDecision.Await);
            return;
        }

        onCompletion.Should().Be(ContinuationDecision.Release, "OnParentCompletion runs after any terminal state");
        onSuccess.Should().Be(parent == JobStatus.Succeeded ? ContinuationDecision.Release : ContinuationDecision.Cancel);
    }

    [Fact]
    public void Continuation_cancel_reasons_and_restorable_reasons()
    {
        ContinuationRules.CancelReason(JobStatus.DeadLettered).Should().Be(JobTerminalReason.ParentFailed);
        ContinuationRules.CancelReason(JobStatus.Cancelled).Should().Be(JobTerminalReason.ParentCancelled);
        Enum.GetValues<JobTerminalReason>().Where(r => ContinuationRules.IsRestorable(r))
            .Should().BeEquivalentTo([JobTerminalReason.ParentFailed, JobTerminalReason.ParentCancelled]);
        ContinuationRules.IsRestorable(null).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Retry_and_requeue_eligibility_over_every_state(JobStatus status)
    {
        AdminRules.IsEligible(AdminRunMode.Retry, status).Should().Be(status == JobStatus.DeadLettered);
        AdminRules.IsEligible(AdminRunMode.Requeue, status).Should().Be(status is JobStatus.DeadLettered or JobStatus.Cancelled);
    }

    [Theory]
    [InlineData(AdminRunMode.Retry, 3, 5, 4)]
    [InlineData(AdminRunMode.Requeue, 3, 5, 8)]
    [InlineData(AdminRunMode.Requeue, 998, 5, 1000)]
    [InlineData(AdminRunMode.Retry, 999, 5, 1000)]
    [InlineData(AdminRunMode.Requeue, 0, 0, 1)]
    public void Attempt_budgets_follow_section_15_11(AdminRunMode mode, int attempt, int typeDefault, int expected)
        => AdminRules.NewMaxAttempts(mode, attempt, typeDefault).Should().Be(expected);

    [Theory]
    [InlineData(AdminRunMode.Retry)]
    [InlineData(AdminRunMode.Requeue)]
    public void No_budget_above_the_persisted_ceiling(AdminRunMode mode)
        => AdminRules.NewMaxAttempts(mode, JobLimits.MaxPersistedAttempts, 5).Should().BeNull();

    [Theory]
    [InlineData(SideEffectStatus.Completed, 1, SideEffectAmbiguity.Retry, SideEffectDecision.Reuse)]
    [InlineData(SideEffectStatus.Completed, 3, SideEffectAmbiguity.DeadLetter, SideEffectDecision.Reuse)]
    [InlineData(SideEffectStatus.Abandoned, 1, SideEffectAmbiguity.MarkUnknown, SideEffectDecision.TakeOverAndIssue)]
    [InlineData(SideEffectStatus.Started, 1, SideEffectAmbiguity.Retry, SideEffectDecision.TakeOverAndIssue)]
    [InlineData(SideEffectStatus.Ambiguous, 1, SideEffectAmbiguity.Retry, SideEffectDecision.TakeOverAndIssue)]
    [InlineData(SideEffectStatus.Started, 2, SideEffectAmbiguity.Retry, SideEffectDecision.MarkUnknown)]
    [InlineData(SideEffectStatus.Started, 1, SideEffectAmbiguity.MarkUnknown, SideEffectDecision.MarkUnknown)]
    [InlineData(SideEffectStatus.Ambiguous, 1, SideEffectAmbiguity.DeadLetter, SideEffectDecision.DeadLetter)]
    public void Ledger_decision_for_an_existing_step(SideEffectStatus status, int issueCount, SideEffectAmbiguity policy, SideEffectDecision expected)
        => SideEffectRules.OnExisting(status, issueCount, policy, maxIssues: 2).Should().Be(expected);

    [Theory]
    [InlineData(SideEffectAmbiguity.Retry, 1, SideEffectDecision.TakeOverAndIssue)]
    [InlineData(SideEffectAmbiguity.Retry, 2, SideEffectDecision.MarkUnknown)]
    [InlineData(SideEffectAmbiguity.MarkUnknown, 1, SideEffectDecision.MarkUnknown)]
    [InlineData(SideEffectAmbiguity.DeadLetter, 1, SideEffectDecision.DeadLetter)]
    public void Ledger_decision_for_an_ambiguous_call(SideEffectAmbiguity policy, int issueCount, SideEffectDecision expected)
        => SideEffectRules.OnAmbiguousCall(policy, issueCount, maxIssues: 2).Should().Be(expected);

    [Fact]
    public void Slot_keys_round_trip_and_reject_caller_keys()
    {
        string group = ConcurrencyKeys.SlotGroup("Email.Send.v1", 5);
        group.Should().Be("ninja:slots:5:Email.Send.v1");
        ConcurrencyKeys.TryParseSlot(group, out string g, out int slots).Should().BeTrue();
        (g, slots).Should().Be((group, 5));
        ConcurrencyKeys.TryParseSlot(ConcurrencyKeys.SlotKey(group, 3), out g, out slots).Should().BeTrue();
        (g, slots).Should().Be((group, 5), "a former slot key maps back to its group");
        ConcurrencyKeys.TryParseSlot("payroll-run:1", out _, out _).Should().BeFalse();
        ConcurrencyKeys.TryParseSlot("ninja:slots:x:Type.v1", out _, out _).Should().BeFalse();
        ConcurrencyKeys.TryParseSlot("ninja:slots:101:Type.v1", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void The_registry_accepts_an_external_ledger_type_with_a_policy_and_refuses_one_without()
    {
        JobHandlerRegistry registry = JobHandlerRegistry.Build([typeof(LedgerHandler), typeof(CappedHandler)], new NinjaQueueOptions());
        registry.TryGetByCode("Unit.Ledger.v1", out JobTypeRegistration? ledger).Should().BeTrue();
        ledger!.UsesLedger.Should().BeTrue();
        ledger.OnAmbiguous.Should().Be(SideEffectAmbiguity.MarkUnknown);
        registry.TryGetByCode("Unit.Capped.v1", out JobTypeRegistration? capped).Should().BeTrue();
        capped!.MaxRunningPerTenant.Should().Be(3);

        FluentActions.Invoking(() => JobHandlerRegistry.Build([typeof(LedgerWithoutPolicy), typeof(FencedLedger), typeof(OverCapped)], new NinjaQueueOptions()))
            .Should().Throw<JobRegistryException>().Which.Errors.Should().SatisfyRespectively(
                e => e.Should().Contain("OnAmbiguous"),
                e => e.Should().Contain("ExternalLedger handler implements exactly one IJobHandler"),
                e => e.Should().Contain("MaxRunningPerTenant"));
    }

    [Fact]
    public void Bulk_requests_are_validated_before_anything_runs()
    {
        JobId[] ids = [.. Enumerable.Range(0, 501).Select(_ => new JobId(Guid.NewGuid()))];
        BulkRequest Valid() => new() { Operation = AdminOperationKind.BulkRetry, JobIds = ids[..500], Reason = "r", IdempotencyKey = "k" };

        JobAdminService.Validate(Valid(), Guid.NewGuid()).JobIds.Should().HaveCount(500);
        JobAdminService.Validate(Valid() with { JobIds = [ids[0], ids[0]] }, Guid.NewGuid()).JobIds.Should().ContainSingle("duplicates are applied once");
        Code(Valid() with { JobIds = ids }).Should().Be(JobAdminErrorCodes.BulkJobIdsLimitExceeded);
        Code(Valid() with { Filter = new BulkJobFilter() }).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        Code(Valid() with { JobIds = null }).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        Code(Valid() with { JobIds = [] }).Should().Be(JobAdminErrorCodes.BulkSelectorRequired);
        Code(Valid() with { Reason = "  " }).Should().Be(JobAdminErrorCodes.ReasonRequired);
        Code(Valid() with { IdempotencyKey = "" }).Should().Be(JobAdminErrorCodes.IdempotencyKeyRequired);
        FluentActions.Invoking(() => JobAdminService.Validate(Valid(), Guid.Empty)).Should().Throw<JobAdminValidationException>()
            .Which.Code.Should().Be(JobAdminErrorCodes.ActorRequired);

        static string Code(BulkRequest request)
            => FluentActions.Invoking(() => JobAdminService.Validate(request, Guid.NewGuid())).Should().Throw<JobAdminValidationException>().Which.Code;
    }

    [Fact]
    public void The_default_redactor_masks_sensitive_fields_at_any_depth()
    {
        string json = """{"subject":"Hi","recipient":"a@b.c","inner":{"note":"n","secret":"s"}}""";
        string redacted = DefaultJobPayloadRedactor.Instance.Redact("Unit.Visible.v1", typeof(VisiblePayload), json);
        redacted.Should().Be("""{"subject":"Hi","recipient":"***","inner":{"note":"n","secret":"***"}}""");
        DefaultJobPayloadRedactor.Instance.Redact("Unit.Visible.v1", typeof(VisiblePayload), "not json").Should().Be("{}");
    }

    public sealed record LedgerPayload(string To);

    [JobType("Unit.Ledger.v1", SideEffects = JobSideEffects.ExternalLedger, Tenancy = JobTenancy.System, OnAmbiguous = SideEffectAmbiguity.MarkUnknown)]
    public sealed class LedgerHandler : IJobHandler<LedgerPayload>
    {
        public Task<JobResult> HandleAsync(LedgerPayload payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());
    }

    public sealed record LedgerPayload2(string To);

    [JobType("Unit.LedgerNoPolicy.v1", SideEffects = JobSideEffects.ExternalLedger, Tenancy = JobTenancy.System)]
    public sealed class LedgerWithoutPolicy : IJobHandler<LedgerPayload2>
    {
        public Task<JobResult> HandleAsync(LedgerPayload2 payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());
    }

    public sealed record FencedLedgerPayload(string To);

    [JobType("Unit.FencedLedger.v1", SideEffects = JobSideEffects.ExternalLedger, Tenancy = JobTenancy.System, OnAmbiguous = SideEffectAmbiguity.Retry)]
    public sealed class FencedLedger : IFencedJobHandler<FencedLedgerPayload>
    {
        public Task<Step> HandleStepAsync(FencedLedgerPayload payload, FencedJobContext context, CancellationToken cancellationToken) => Task.FromResult(Step.Complete());
    }

    public sealed record CappedPayload(Guid TenantId);

    [JobType("Unit.Capped.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, MaxRunningPerTenant = 3)]
    public sealed class CappedHandler : IJobHandler<CappedPayload>
    {
        public Task<JobResult> HandleAsync(CappedPayload payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());
    }

    public sealed record OverCappedPayload(Guid TenantId);

    [JobType("Unit.OverCapped.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, MaxRunningPerTenant = 101)]
    public sealed class OverCapped : IJobHandler<OverCappedPayload>
    {
        public Task<JobResult> HandleAsync(OverCappedPayload payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());
    }

    public sealed record VisiblePayload(string Subject, [property: Sensitive] string Recipient, VisibleInner Inner);

    public sealed record VisibleInner(string Note, [property: Sensitive] string Secret);
}