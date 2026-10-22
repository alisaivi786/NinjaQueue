using System.Collections.Concurrent;
using NinjaQueue.Execution;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>What the dead-letter test handlers saw, and how they should misbehave.</summary>
public sealed class DeadLetterProbe
{
    /// <summary>Dead-letter handler invocations per job (committed or not).</summary>
    public ConcurrentDictionary<Guid, int> Calls { get; } = new();

    /// <summary>(job, error code, payload readable) per invocation.</summary>
    public ConcurrentQueue<(Guid Job, string? ErrorCode, bool PayloadReadable)> Seen { get; } = new();

    /// <summary>The next N handler invocations write, then throw.</summary>
    public int FailNext;

    /// <summary>Every handler invocation writes, then throws.</summary>
    public bool AlwaysFail { get; set; }

    /// <summary>Delay inside the handler, after its write (widens race windows).</summary>
    public int DelayMs { get; set; }

    internal async Task RecordAsync(WidgetProvider widgets, JobDeadLetterContext context, string suffix, bool readable, CancellationToken ct)
    {
        Calls.AddOrUpdate(context.JobId.Value, 1, (_, n) => n + 1);
        Seen.Enqueue((context.JobId.Value, context.ErrorCode, readable));
        await widgets.AddAsync($"{context.JobId.Value:N}-{suffix}", ct);
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, ct);
        }

        if (AlwaysFail || Interlocked.Decrement(ref FailNext) >= 0)
        {
            throw new InvalidOperationException("Injected dead-letter handler failure after its write.");
        }
    }
}

public sealed record DlPayload(string Mode);

/// <summary>A standard job whose handler class is also its dead-letter handler (the HCM shape).</summary>
[JobType("Test.DL.Standard.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 2)]
public sealed class DlStandardHandler(WidgetProvider widgets, DeadLetterProbe probe) : IJobHandler<DlPayload>, IJobDeadLetterHandler<DlPayload>
{
    public Task<JobResult> HandleAsync(DlPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Mode switch
        {
            "succeed" => Task.FromResult(JobResult.Success()),
            "permanent" => throw new PermanentJobException("Injected permanent failure."),
            _ => throw new InvalidOperationException("Injected failure."),
        };
    }

    public Task OnDeadLetteredAsync(DlPayload payload, JobDeadLetterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return probe.RecordAsync(widgets, context, "dl", readable: true, cancellationToken);
    }

    public Task OnPayloadUnreadableAsync(JobDeadLetterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return probe.RecordAsync(widgets, context, "dl-unreadable", readable: false, cancellationToken);
    }
}

public sealed record DlFencedPayload(string Note);

/// <summary>A fenced job whose every step fails after a write; its dead-letter handler is the same class.</summary>
[JobType("Test.DL.Fenced.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 2, StepTimeout = "00:00:20")]
public sealed class DlFencedHandler(WidgetProvider widgets, DeadLetterProbe probe) : IFencedJobHandler<DlFencedPayload>, IJobDeadLetterHandler<DlFencedPayload>
{
    public async Task<Step> HandleStepAsync(DlFencedPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await widgets.AddAsync($"{context.JobId.Value:N}-step", cancellationToken);
        throw new InvalidOperationException("Injected step failure.");
    }

    public Task OnDeadLetteredAsync(DlFencedPayload payload, JobDeadLetterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return probe.RecordAsync(widgets, context, "dl", readable: true, cancellationToken);
    }
}

public sealed record DlSeparatePayload(string Note);

/// <summary>A job whose dead-letter handler is a separate service.</summary>
[JobType("Test.DL.Separate.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 1)]
public sealed class DlSeparateJobHandler : IJobHandler<DlSeparatePayload>
{
    public Task<JobResult> HandleAsync(DlSeparatePayload payload, JobContext context, CancellationToken cancellationToken)
        => throw new PermanentJobException("Injected permanent failure.");
}

public sealed class DlSeparateDeadLetterHandler(WidgetProvider widgets, DeadLetterProbe probe) : IJobDeadLetterHandler<DlSeparatePayload>
{
    public Task OnDeadLetteredAsync(DlSeparatePayload payload, JobDeadLetterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return probe.RecordAsync(widgets, context, "dl", readable: true, cancellationToken);
    }
}

/// <summary>Invalid registrations for the startup-validation tests (registered explicitly, never scanned).</summary>
public sealed record DlUnregisteredPayload(string Note);

public sealed class DlOrphanDeadLetterHandler : IJobDeadLetterHandler<DlUnregisteredPayload>
{
    public Task OnDeadLetteredAsync(DlUnregisteredPayload payload, JobDeadLetterContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JobType("Test.DL.Mismatch.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System)]
public sealed class DlMismatchHandler : IJobHandler<DlFencedPayload>, IJobDeadLetterHandler<DlUnregisteredPayload>
{
    public Task<JobResult> HandleAsync(DlFencedPayload payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());

    public Task OnDeadLetteredAsync(DlUnregisteredPayload payload, JobDeadLetterContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// <see cref="IJobDeadLetterHandler{TPayload}"/> (plan §18 F7): the handler runs exactly once per dead-lettered job, whichever
/// path dead-lettered it, inside an engine-owned transaction with the marker, so its writes commit exactly once; the sweep
/// covers a crash before the marker and retries failures a bounded number of times. PostgreSQL 16, SQL Server RCSI ON / OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
public sealed class DeadLetterHandlerTests(JobsDatabaseFixture fixture)
{
    private const string Standard = "Test.DL.Standard.v1";
    private const string Fenced = "Test.DL.Fenced.v1";
    private const string Separate = "Test.DL.Separate.v1";

    private static async Task<Phase4Host> HostAsync(JobsDatabaseFixture fixture, DatabaseKind kind, Action<NinjaQueueOptions>? configure = null)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        return Build(db, configure);
    }

    private static Phase4Host Build(TestDatabase db, Action<NinjaQueueOptions>? configure = null)
        => Phase4Host.Build(db, configure, b =>
        {
            b.Services.AddSingleton<DeadLetterProbe>();
            b.AddJobHandler<DlStandardHandler>().AddJobHandler<DlFencedHandler>().AddJobHandler<DlSeparateJobHandler>()
                .AddDeadLetterHandler<DlSeparateDeadLetterHandler>();
        });

    private static DeadLetterProbe Probe(Phase4Host host) => host.Get<DeadLetterProbe>();

    private static async Task<(DateTimeOffset? HandledAt, int? Attempts)> MarkerAsync(Phase4Host host, JobId id)
    {
        object?[] row = (await host.Database.QueryAsync(
            $"SELECT dead_letter_handled_at_utc, dead_letter_attempts FROM ninjaqueue.background_jobs WHERE guid_id = '{id.Value}'")).Single();
        return (row[0] is null ? null : DateTimeOffset.MinValue, row[1] is null ? null : Phase4Host.Int(row[1]));
    }

    private static Task<int> CountEventsAsync(Phase4Host host, JobId id, string code)
        => host.Database.ScalarAsync<int>($"""
            SELECT count(*) FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id = '{id.Value}' AND e.event_code = '{code}'
            """);

    private static async Task AssertHandledOnceAsync(Phase4Host host, JobId id, string suffix = "dl")
    {
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.WidgetsForAsync(id, suffix)).Should().Be(1, "the handler's write committed exactly once");
        (await MarkerAsync(host, id)).HandledAt.Should().NotBeNull("the marker committed with it");
        (await CountEventsAsync(host, id, JobEventCodes.JobDeadLetterHandled)).Should().Be(1);
        (await host.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(0, "nothing is left for the sweep");
        (await host.WidgetsForAsync(id, suffix)).Should().Be(1);
    }

    /// <summary>Dead-letters a job without running its handler: the state a crash right after the dead-letter commit leaves.</summary>
    private static async Task<JobId> DeadLetterWithoutTriggerAsync(Phase4Host host)
    {
        JobId id = await host.EnqueueAsync(new DlPayload("permanent"));
        ClaimedJob job = (await host.ClaimOneAsync(Standard))!;
        (await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RecordOutcomeAsync(job,
            new AttemptOutcome(AttemptOutcomeKind.DeadLetter, ErrorCode: "Permanent", Error: "Injected.", TerminalReason: JobTerminalReason.PermanentError),
            CancellationToken.None))).Should().Be(OutcomeResult.Recorded);
        return id;
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Executor_last_attempt_runs_the_handler_once(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new DlPayload("fail"));

        (await host.RunUntilTerminalAsync(id, Standard)).Should().Be(JobStatus.DeadLettered);

        await AssertHandledOnceAsync(host, id);
        Probe(host).Calls[id.Value].Should().Be(1);
        (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.Exhausted);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Permanent_error_runs_the_handler_once_with_the_error_code(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new DlPayload("permanent"));

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.DeadLettered);

        await AssertHandledOnceAsync(host, id);
        Probe(host).Seen.Should().ContainSingle(s => s.Job == id.Value && s.ErrorCode == "Permanent" && s.PayloadReadable);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Fenced_job_dead_lettered_at_its_last_attempt_runs_the_handler_once(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new DlFencedPayload("f"));

        (await host.RunUntilTerminalAsync(id, Fenced)).Should().Be(JobStatus.DeadLettered);

        await AssertHandledOnceAsync(host, id);
        (await host.WidgetsForAsync(id, "step")).Should().Be(0, "every failed step rolled back");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Invalid_payload_runs_the_payload_less_handler_once(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new DlPayload("succeed"));
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET payload = '{{\"mode\": 42}}' WHERE guid_id = '{id.Value}'");

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.DeadLettered);

        await AssertHandledOnceAsync(host, id, "dl-unreadable");
        Probe(host).Seen.Should().ContainSingle(s => s.Job == id.Value && s.ErrorCode == "InvalidPayload" && !s.PayloadReadable);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Lease_expiry_recovery_at_the_last_attempt_is_handled_by_the_sweep(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new DlPayload("succeed"), new JobOptions { MaxAttempts = 1 });
        (await host.ClaimAsync([Standard], lease: TimeSpan.FromMilliseconds(200))).Should().ContainSingle("the worker 'crashes' holding the lease");
        await Task.Delay(500);

        (await host.RecoverAsync()).Should().Be(1);
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await MarkerAsync(host, id)).HandledAt.Should().BeNull("recovery only dead-letters; the sweep runs the handler");

        (await host.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(1);
        await AssertHandledOnceAsync(host, id);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task The_maintenance_loop_sweeps_a_recovered_dead_letter(DatabaseKind kind)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        await using Phase4Host crashed = Build(db);
        JobId id = await crashed.EnqueueAsync(new DlPayload("succeed"), new JobOptions { MaxAttempts = 1 });
        (await crashed.ClaimAsync([Standard], lease: TimeSpan.FromMilliseconds(200))).Should().ContainSingle();

        await using Phase4Host worker = Build(db, o => o.Worker.MaintenanceInterval = TimeSpan.FromMilliseconds(200));
        await worker.StartWorkersAsync();
        await ConcurrencySuiteTests.WaitAsync(async () => await worker.WidgetsForAsync(id, "dl") == 1, TimeSpan.FromSeconds(30));
        await worker.StopWorkersAsync();

        await AssertHandledOnceAsync(worker, id);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_crash_before_the_marker_is_recovered_by_the_sweep(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId id = await DeadLetterWithoutTriggerAsync(host);
        (await MarkerAsync(host, id)).HandledAt.Should().BeNull();
        Probe(host).Calls.Should().NotContainKey(id.Value);

        (await host.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(1);

        await AssertHandledOnceAsync(host, id);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Racing_triggers_commit_the_handler_exactly_once(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        Probe(host).DelayMs = 200;
        JobId id = await DeadLetterWithoutTriggerAsync(host);
        long dbId = await host.DbIdAsync(id);
        DeadLetterDispatcher dispatcher = host.Get<DeadLetterDispatcher>();

        DeadLetterRun[] runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => dispatcher.RunAsync(dbId, Standard, CancellationToken.None))));
        int swept = await dispatcher.SweepAsync(CancellationToken.None);

        runs.Count(r => r.Status == DeadLetterRunStatus.Handled).Should().Be(1);
        runs.Where(r => r.Status != DeadLetterRunStatus.Handled).Should().OnlyContain(r => r.Status == DeadLetterRunStatus.NotPending);
        swept.Should().Be(0);
        Probe(host).Calls[id.Value].Should().Be(1, "the losers matched 0 rows at the marker claim and never ran the handler");
        await AssertHandledOnceAsync(host, id);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_failing_handler_rolls_back_and_the_sweep_retries_it(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        Probe(host).FailNext = 1;
        JobId id = await host.EnqueueAsync(new DlPayload("permanent"));

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.DeadLettered, "a handler failure never changes the job's outcome");
        (await host.WidgetsForAsync(id, "dl")).Should().Be(0, "the failed handler's write rolled back with the marker");
        (await MarkerAsync(host, id)).Should().Be(((DateTimeOffset?)null, (int?)1));
        (await CountEventsAsync(host, id, JobEventCodes.JobDeadLetterHandlerFailed)).Should().Be(1);

        (await host.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(1);

        await AssertHandledOnceAsync(host, id);
        Probe(host).Calls[id.Value].Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_handler_that_keeps_failing_is_abandoned_after_the_bounded_attempts(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind, o => o.Retry.MaxDeadLetterHandlerAttempts = 3);
        Probe(host).AlwaysFail = true;
        JobId id = await host.EnqueueAsync(new DlPayload("permanent"));
        DeadLetterDispatcher dispatcher = host.Get<DeadLetterDispatcher>();

        (await host.RunOneAsync(Standard)).Should().Be(ExecutionResult.DeadLettered);
        (await dispatcher.SweepAsync(CancellationToken.None)).Should().Be(1);
        (await dispatcher.SweepAsync(CancellationToken.None)).Should().Be(1);
        (await dispatcher.SweepAsync(CancellationToken.None)).Should().Be(0, "abandoned after 3 failed runs");

        Probe(host).Calls[id.Value].Should().Be(3);
        (await host.WidgetsForAsync(id, "dl")).Should().Be(0);
        (await MarkerAsync(host, id)).Should().Be(((DateTimeOffset?)null, (int?)3));
        (await CountEventsAsync(host, id, JobEventCodes.JobDeadLetterHandlerFailed)).Should().Be(3);
        (await CountEventsAsync(host, id, JobEventCodes.JobDeadLetterHandlerAbandoned)).Should().Be(1);
        (await CountEventsAsync(host, id, JobEventCodes.JobDeadLetterHandled)).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_separately_registered_handler_runs_and_a_type_without_one_is_settled(DatabaseKind kind)
    {
        await using Phase4Host host = await HostAsync(fixture, kind);
        JobId separate = await host.EnqueueAsync(new DlSeparatePayload("s"));
        JobId plain = await host.EnqueueAsync(new P4Payload("permanent"));

        (await host.RunOneAsync(Separate)).Should().Be(ExecutionResult.DeadLettered);
        (await host.RunOneAsync("Test.P4.Standard.v1")).Should().Be(ExecutionResult.DeadLettered);

        await AssertHandledOnceAsync(host, separate);
        (await MarkerAsync(host, plain)).HandledAt.Should().NotBeNull("a known type without a handler is settled, so a handler added later never replays it");
        (await CountEventsAsync(host, plain, JobEventCodes.JobDeadLetterHandled)).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_type_this_instance_does_not_know_is_left_for_one_that_does(DatabaseKind kind)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        await using Phase4Host capable = Build(db);
        JobId id = await DeadLetterWithoutTriggerAsync(capable);

        // An older instance without the Test.DL.* types (rolling deploy): it must neither run nor settle the dead letter.
        await using Phase4Host old = Phase4Host.Build(db);
        (await old.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(0);
        (await old.Get<DeadLetterDispatcher>().RunAsync(await old.DbIdAsync(id), Standard, CancellationToken.None)).Status.Should().Be(DeadLetterRunStatus.NotPending);
        (await MarkerAsync(capable, id)).HandledAt.Should().BeNull();

        (await capable.Get<DeadLetterDispatcher>().SweepAsync(CancellationToken.None)).Should().Be(1);
        await AssertHandledOnceAsync(capable, id);
    }

    [Fact]
    public void Startup_validation_discovers_and_checks_dead_letter_handlers()
    {
        NinjaQueueOptions options = new();
        JobHandlerRegistry registry = JobHandlerRegistry.Build([typeof(DlStandardHandler), typeof(DlSeparateJobHandler), typeof(P4StandardHandler)],
            [typeof(DlSeparateDeadLetterHandler)], options);
        registry.TryGetByCode(Standard, out JobTypeRegistration? standard).Should().BeTrue();
        standard!.DeadLetterHandlerType.Should().Be<DlStandardHandler>();
        registry.TryGetByCode(Separate, out JobTypeRegistration? separate).Should().BeTrue();
        separate!.DeadLetterHandlerType.Should().Be<DlSeparateDeadLetterHandler>();
        registry.TryGetByCode("Test.P4.Standard.v1", out JobTypeRegistration? none).Should().BeTrue();
        none!.DeadLetterHandlerType.Should().BeNull();

        FluentActions.Invoking(() => JobHandlerRegistry.Build([typeof(DlStandardHandler)], [typeof(DlOrphanDeadLetterHandler)], options))
            .Should().Throw<JobRegistryException>().Which.Errors.Should().ContainSingle(e => e.Contains("has no registered job type", StringComparison.Ordinal));
        FluentActions.Invoking(() => JobHandlerRegistry.Build([typeof(DlMismatchHandler)], [], options))
            .Should().Throw<JobRegistryException>().Which.Errors.Should().ContainSingle(e => e.Contains("only for its own payload", StringComparison.Ordinal));
        FluentActions.Invoking(() => JobHandlerRegistry.Build([typeof(DlStandardHandler)], [typeof(DlSeparateDeadLetterHandler), typeof(DlStandardHandler)], options))
            .Should().Throw<JobRegistryException>().Which.Errors.Should().ContainSingle(e => e.Contains("has no registered job type", StringComparison.Ordinal),
                "DlSeparatePayload is not registered here; DlStandardHandler listed again as a separate handler is the same class and allowed");
    }
}