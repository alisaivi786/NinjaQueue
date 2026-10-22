using NinjaQueue.Admin;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Phase 5 exit, queued cancel (§14.4 first row, §17.8): a queued job is Cancelled at once with its queue row removed and a
/// <c>JobCancelled</c> event carrying the actor and the reason; a claimed job is refused in Release 1
/// (<c>EnableRunningJobCancel = false</c>); tenant scoping is enforced on the server.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "5")]
public sealed class CancelTests(JobsDatabaseFixture fixture)
{
    private const string Probe = "Test.Probe.v1";
    private static readonly Guid Actor = Guid.Parse("dddddddd-0000-0000-0000-00000000000d");

    private Task<JobsHost> HostAsync(DatabaseKind kind, Action<NinjaQueueBuilder>? more = null)
        => JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            b.Services.AddSingleton<ProbePlan>();
            more?.Invoke(b);
        });

    private static Task<CancelResult> CancelAsync(JobsHost host, JobId id, string reason = "No longer needed")
        => host.InScopeAsync(sp => sp.GetRequiredService<IJobAdminService>().CancelAsync(id, Actor, reason));

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Queued_job_is_cancelled_at_once_with_actor_and_reason(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId queued = await host.EnqueueAsync(new ProbePayload("queued"));
        JobId scheduled = await host.EnqueueAsync(new ProbePayload("scheduled"), new JobOptions { RunAt = DateTimeOffset.UtcNow.AddHours(1) });

        (await CancelAsync(host, queued, "Duplicate request")).Should().Be(CancelResult.Cancelled);
        (await CancelAsync(host, scheduled)).Should().Be(CancelResult.Cancelled);

        (await host.StatusAsync(queued)).Should().Be(JobStatus.Cancelled);
        (await host.StatusAsync(scheduled)).Should().Be(JobStatus.Cancelled);
        (await host.CountQueueAsync(queued)).Should().Be(0);
        (await host.CountQueueAsync(scheduled)).Should().Be(0);
        (await host.Database.JobColumnAsync<short>(queued, "terminal_reason_code")).Should().Be((short)JobTerminalReason.AdminCancelled);
        (await host.Database.JobColumnAsync<string>(queued, "cancel_reason")).Should().Be("Duplicate request");
        (await host.EventsAsync(queued)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobCancelled);
        object?[] evt = (await host.Database.QueryAsync($"""
            SELECT e.actor_id, e.reason FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id
             WHERE b.guid_id = '{queued.Value}' AND e.event_code = '{JobEventCodes.JobCancelled}'
            """)).Single();
        evt[0].Should().Be(Actor);
        evt[1].Should().Be("Duplicate request");
        (await host.AttemptsAsync(queued)).Should().Be((0, 0, 3));

        (await host.RunOneAsync(Probe)).Should().BeNull("a cancelled job never runs");
        host.Get<ProbePlan>().Calls.Should().Be(0);

        // Idempotent: a second cancel reports the terminal state and records the late request.
        (await CancelAsync(host, queued)).Should().Be(CancelResult.AlreadyCompleted);
        (await host.EventsAsync(queued)).Should().EndWith(JobEventCodes.JobCancelTooLate);
        (await host.StatusAsync(queued)).Should().Be(JobStatus.Cancelled);

        (await CancelAsync(host, new JobId(Guid.NewGuid()))).Should().Be(CancelResult.NotFound);
        await FluentActions.Awaiting(() => CancelAsync(host, queued, " ")).Should().ThrowAsync<NinjaQueue.Admin.JobAdminValidationException>("a reason is required (the admin API's validation error, §17.8)");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Claimed_job_cancel_is_not_supported_in_release_1(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await host.EnqueueAsync(new ProbePayload("running"));
        ClaimedJob job = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;

        (await CancelAsync(host, id)).Should().Be(CancelResult.RunningNotSupported);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing);
        (await host.Database.IsNullAsync(id, "cancel_requested_at_utc")).Should().BeTrue("nothing is recorded for a refused cancel");
        (await host.Database.QueueStateAsync(id))!.Value.Leased.Should().BeTrue("the queue row and its lease are untouched");
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed);

        (await host.ExecuteAsync(job)).Should().Be(ExecutionResult.Succeeded, "the running attempt finishes normally");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancel_outside_the_callers_tenants_is_reported_as_not_found(DatabaseKind kind)
    {
        Guid tenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000c1");
        Guid tenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000c2");
        await using JobsHost host = await HostAsync(kind, b =>
        {
            b.AddJobHandler<CreateWidgetsHandler>();
            b.Services.AddSingleton<IJobTenantAccessor>(new FixedTenants([tenantA]));
        });
        JobId other = await host.EnqueueAsync(new WidgetPayload(tenantB, "b"), new JobOptions { TenantId = tenantB });
        JobId own = await host.EnqueueAsync(new WidgetPayload(tenantA, "a"), new JobOptions { TenantId = tenantA });

        (await CancelAsync(host, other)).Should().Be(CancelResult.NotFound);
        (await CancelAsync(host, own)).Should().Be(CancelResult.Cancelled);

        (await host.StatusAsync(other)).Should().Be(JobStatus.Enqueued);
        (await host.EventsAsync(other)).Should().Equal(JobEventCodes.JobEnqueued);
        (await host.StatusAsync(own)).Should().Be(JobStatus.Cancelled);
    }

    private sealed class FixedTenants(IReadOnlyCollection<Guid> tenants) : IJobTenantAccessor
    {
        public IReadOnlyCollection<Guid>? AllowedTenantScopes { get; } = tenants;
    }
}