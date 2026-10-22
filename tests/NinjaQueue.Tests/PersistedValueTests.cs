namespace NinjaQueue.Tests;

/// <summary>
/// G13 (part 2), §15.10: every persisted value is pinned. A value is never renumbered or reused, so changing one of these
/// numbers must fail CI. Adding a value means adding it here, and widening the CHECK in a new schema version.
/// </summary>
public sealed class PersistedValueTests
{
    [Fact]
    public void Job_status_values_are_pinned()
        => Pinned<JobStatus>(
            (JobStatus.Enqueued, 1), (JobStatus.Scheduled, 2), (JobStatus.Awaiting, 3), (JobStatus.Processing, 4),
            (JobStatus.Succeeded, 10), (JobStatus.Cancelled, 11), (JobStatus.DeadLettered, 12));

    [Fact]
    public void Terminal_is_status_at_or_above_ten()
    {
        Enum.GetValues<JobStatus>().Where(x => x.IsTerminal())
            .Should().BeEquivalentTo([JobStatus.Succeeded, JobStatus.Cancelled, JobStatus.DeadLettered]);
        JobStatusExtensions.FirstTerminalValue.Should().Be(10);
    }

    [Fact]
    public void Attempt_outcome_values_are_pinned()
        => Pinned<JobAttemptOutcome>(
            (JobAttemptOutcome.Succeeded, 1), (JobAttemptOutcome.Failed, 2), (JobAttemptOutcome.TimedOut, 3),
            (JobAttemptOutcome.LeaseLost, 4), (JobAttemptOutcome.Cancelled, 5), (JobAttemptOutcome.Snoozed, 6),
            (JobAttemptOutcome.Released, 7), (JobAttemptOutcome.Deferred, 8));

    [Fact]
    public void Terminal_reason_values_are_pinned()
        => Pinned<JobTerminalReason>(
            (JobTerminalReason.Completed, 1), (JobTerminalReason.Exhausted, 2), (JobTerminalReason.PermanentError, 3),
            (JobTerminalReason.AdminCancelled, 4), (JobTerminalReason.ParentFailed, 5), (JobTerminalReason.ParentCancelled, 6),
            (JobTerminalReason.MaxAgeExceeded, 7), (JobTerminalReason.HandlerCancelled, 8));

    [Fact]
    public void Continuation_policy_values_are_pinned()
        => Pinned<ContinuationPolicy>((ContinuationPolicy.OnParentSuccess, 1), (ContinuationPolicy.OnParentCompletion, 2));

    [Fact]
    public void Misfire_policy_values_are_pinned()
        => Pinned<MisfirePolicy>((MisfirePolicy.FireOnce, 1), (MisfirePolicy.FireAll, 2), (MisfirePolicy.Skip, 3));

    [Fact]
    public void Side_effect_status_values_are_pinned()
        => Pinned<SideEffectStatus>(
            (SideEffectStatus.Started, 1), (SideEffectStatus.Completed, 2), (SideEffectStatus.Ambiguous, 3), (SideEffectStatus.Abandoned, 4));

    [Fact]
    public void Admin_operation_values_are_pinned()
    {
        Pinned<AdminOperationKind>((AdminOperationKind.BulkRetry, 1), (AdminOperationKind.BulkRequeue, 2), (AdminOperationKind.BulkCancel, 3));
        Pinned<AdminOperationStatus>(
            (AdminOperationStatus.Pending, 1), (AdminOperationStatus.Running, 2), (AdminOperationStatus.Completed, 3), (AdminOperationStatus.Failed, 4));
    }

    [Fact]
    public void Persisted_enums_are_stored_as_smallint()
    {
        foreach (Type type in (Type[])[typeof(JobStatus), typeof(JobAttemptOutcome), typeof(JobTerminalReason), typeof(ContinuationPolicy),
                     typeof(MisfirePolicy), typeof(SideEffectStatus), typeof(AdminOperationKind), typeof(AdminOperationStatus)])
        {
            Enum.GetUnderlyingType(type).Should().Be<short>(type.Name);
        }
    }

    [Fact]
    public void Attempt_limits_are_pinned()
    {
        JobLimits.MaxPersistedAttempts.Should().Be(1000);
        JobLimits.MaxConfiguredAttempts.Should().Be(100);
        JobLimits.DefaultMaxAttempts.Should().Be(5);
        JobLimits.SystemTenantScope.Should().Be(Guid.Empty);
    }

    private static void Pinned<TEnum>(params (TEnum Value, short Expected)[] pinned)
        where TEnum : struct, Enum
    {
        Enum.GetValues<TEnum>().Should().HaveCount(pinned.Length, $"every {typeof(TEnum).Name} value must be pinned here");
        foreach ((TEnum value, short expected) in pinned)
        {
            Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(expected, $"{typeof(TEnum).Name}.{value}");
        }
    }
}