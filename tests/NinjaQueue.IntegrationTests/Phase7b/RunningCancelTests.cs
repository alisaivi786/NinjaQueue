using System.Security.Cryptography;
using NinjaQueue.Admin;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// Cooperative cancel of a running job (§14.4, Release 2) through the real worker path (claim loop, renewer, executor):
/// the renewal fires AdminCancel; a cooperative handler ends Cancelled (not charged); success racing the cancel wins with
/// <c>JobCancelTooLate</c>; a handler that ignores the token is fenced after the grace period and recovery cancels it; a
/// non-interruptible type finishes its attempt. Short leases, so the host registers only the standard handlers it needs.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
public sealed class RunningCancelTests(JobsDatabaseFixture fixture)
{
    private async Task<WorkerTestHost> HostAsync(DatabaseKind kind, bool enabled = true)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        return WorkerTestHost.Create(db,
            b =>
            {
                b.AddJobHandler<P7bStepHandler>().AddJobHandler<P7bNonInterruptibleHandler>();
                b.Services.AddSingleton<P7bPlan>();
                b.Services.AddSingleton<RecordingAuditSink>();
                b.Services.AddSingleton<IJobAuditSink>(sp => sp.GetRequiredService<RecordingAuditSink>());
                b.Services.AddSingleton<CapturingLoggerProvider>();
                b.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp => sp.GetRequiredService<CapturingLoggerProvider>());
            },
            o =>
            {
                o.EnableRunningJobCancel = enabled;
                // The renewer starts from lease - 2 s (clock skew), so the lease must leave room for margin + one interval.
                o.Worker.LeaseDuration = TimeSpan.FromSeconds(6);
                o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(1);
                o.Worker.CancelGracePeriod = TimeSpan.FromSeconds(1.5);
                o.Worker.MaintenanceInterval = TimeSpan.FromMinutes(10);
                o.Worker.ShutdownDrainPeriod = TimeSpan.FromSeconds(3);
                o.Worker.ShutdownCancelPeriod = TimeSpan.FromSeconds(3);
                o.Worker.ShutdownCleanupPeriod = TimeSpan.FromSeconds(3);
            });
    }

    private static Task<CancelResult> CancelAsync(WorkerTestHost host, JobId id, string reason = "test cancel")
        => host.InScopeAsync(sp => sp.GetRequiredService<IJobAdminService>().CancelAsync(id, P7b.Actor, reason));

    private static async Task<ExecutionResult> ExecuteAsync(WorkerTestHost host, ClaimedJob job)
    {
        using JobSignals signals = new(CancellationToken.None);
        return await host.Get<JobExecutor>().ExecuteAsync(job, signals);
    }

    private static P7bPlan Plan(WorkerTestHost host) => host.Get<P7bPlan>();

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_cooperative_handler_stops_and_the_job_ends_cancelled_without_a_charge(DatabaseKind kind)
    {
        await using WorkerTestHost host = await HostAsync(kind);
        Plan(host).Step = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new P7bPayload("coop"));
        await host.StartAsync();
        await Plan(host).Started("coop").WaitAsync(TimeSpan.FromSeconds(20));

        (await CancelAsync(host, id, "operator stop")).Should().Be(CancelResult.CancelRequested);
        (await CancelAsync(host, id)).Should().Be(CancelResult.AlreadyRequested);
        await P7b.WaitForJobAsync(host.Database, id, async () => await host.Database.StatusOfAsync(id) == JobStatus.Cancelled, 20,
            "the renewal fires AdminCancel within a renewal interval; log: " + string.Join(" | ", host.Get<CapturingLoggerProvider>().Lines));

        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.AdminCancelled);
        (await host.Database.AttemptsOfAsync(id)).Should().Be((0, 0, 3), "a cooperative cancel is not charged");
        (await host.Database.EventsOfAsync(id)).Should().ContainInOrder(JobEventCodes.JobCancelRequested, JobEventCodes.JobCancelled);
        await host.StopAsync();
        await P7b.AssertInvariantsAsync(host.Database);
        host.Get<RecordingAuditSink>().Entries.Should().Contain(e => e.Action == "Cancel" && e.Outcome == nameof(CancelResult.CancelRequested) && e.Reason == "operator stop");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Success_racing_the_cancel_wins_with_cancel_too_late(DatabaseKind kind)
    {
        await using WorkerTestHost host = await HostAsync(kind);

        // Deterministic: the cancel is committed while the attempt runs, the handler never looks at its token.
        JobId id = await host.EnqueueAsync(new P7bPayload("race"));
        ClaimedJob job = (await host.ClaimOneAsync(P7b.Step))!;
        (await CancelAsync(host, id)).Should().Be(CancelResult.CancelRequested);
        (await ExecuteAsync(host, job)).Should().Be(ExecutionResult.Succeeded);
        (await host.Database.EventsOfAsync(id)).Should().ContainInOrder(JobEventCodes.JobCancelRequested, JobEventCodes.JobSucceeded, JobEventCodes.JobCancelTooLate);

        // Racing: cancel and completion at the same time, repeatedly; each job ends in exactly one consistent outcome.
        Plan(host).Step = async (_, _, _) =>
        {
            await Task.Delay(RandomNumberGenerator.GetInt32(15), CancellationToken.None);
            return JobResult.Success();
        };
        for (int i = 0; i < 15; i++)
        {
            JobId racing = await host.EnqueueAsync(new P7bPayload($"r{i}"));
            ClaimedJob claimed = (await host.ClaimOneAsync(P7b.Step))!;
            Task<ExecutionResult> run = ExecuteAsync(host, claimed);
            Task<CancelResult> cancel = CancelAsync(host, racing);
            await Task.WhenAll(run, cancel);
            CancelResult cancelled = await cancel;
            List<string> events = await host.Database.EventsOfAsync(racing);
            (await host.Database.StatusOfAsync(racing)).Should().Be(JobStatus.Succeeded, "without a renewal the attempt never sees the cancel, so success wins");
            (cancelled is CancelResult.CancelRequested ? events.Contains(JobEventCodes.JobCancelTooLate) : cancelled == CancelResult.AlreadyCompleted)
                .Should().BeTrue($"the admin sees 'already completed' one way or the other ({cancelled})");
        }

        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_handler_that_ignores_the_token_is_fenced_and_recovery_cancels_it(DatabaseKind kind)
    {
        await using WorkerTestHost host = await HostAsync(kind);
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Plan(host).Step = async (_, _, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12), CancellationToken.None);
            finished.TrySetResult();
            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new P7bPayload("deaf"));
        await host.StartAsync();
        await Plan(host).Started("deaf").WaitAsync(TimeSpan.FromSeconds(20));
        (await CancelAsync(host, id)).Should().Be(CancelResult.CancelRequested);

        // After the grace period the worker stops renewing; once the lease expires, recovery sees cancel_requested.
        await P7b.WaitForJobAsync(host.Database, id, async () => (await host.Database.QueueStateAsync(id))?.Expired == true, 30, "the lease is no longer renewed");
        (await host.RecoverAsync()).Should().Be(1);
        (await host.Database.StatusOfAsync(id)).Should().Be(JobStatus.Cancelled);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await host.StopAsync();
        (await host.Database.StatusOfAsync(id)).Should().Be(JobStatus.Cancelled, "the late success of a fenced-off worker never commits");
        (await host.Database.EventsOfAsync(id)).Should().NotContain(JobEventCodes.JobSucceeded).And.NotContain(JobEventCodes.JobRetryScheduled);
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_non_interruptible_attempt_finishes_and_the_cancel_only_stops_retries(DatabaseKind kind)
    {
        await using WorkerTestHost host = await HostAsync(kind);
        bool tokenFired = false;
        int attempts = 0;
        Plan(host).Step = async (payload, _, ct) =>
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
            tokenFired |= ct.IsCancellationRequested;
            return payload.Name == "fails" ? throw new InvalidOperationException("retryable") : JobResult.Success();
        };
        JobId ok = await host.EnqueueAsync(new P7bNonInterruptiblePayload("ok"));
        await host.StartAsync();
        await Plan(host).Started("ok").WaitAsync(TimeSpan.FromSeconds(20));
        (await CancelAsync(host, ok)).Should().Be(CancelResult.CancelAfterCurrentAttempt);
        await P7b.WaitForJobAsync(host.Database, ok, async () => (await host.Database.StatusOfAsync(ok))?.IsTerminal() == true, 20);
        (await host.Database.StatusOfAsync(ok)).Should().Be(JobStatus.Succeeded);
        (await host.Database.EventsOfAsync(ok)).Should().Contain(JobEventCodes.JobCancelTooLate);

        JobId fails = await host.EnqueueAsync(new P7bNonInterruptiblePayload("fails"));
        await Plan(host).Started("fails").WaitAsync(TimeSpan.FromSeconds(20));
        (await CancelAsync(host, fails)).Should().Be(CancelResult.CancelAfterCurrentAttempt);
        await P7b.WaitForJobAsync(host.Database, fails, async () => (await host.Database.StatusOfAsync(fails))?.IsTerminal() == true, 20);
        await host.StopAsync();

        (await host.Database.StatusOfAsync(fails)).Should().Be(JobStatus.Cancelled, "a pending cancel is never rescheduled (§14.4 step 5)");
        tokenFired.Should().BeFalse("a non-interruptible attempt's token never fires for a cancel");
        attempts.Should().Be(2, "one attempt each; the failed one is not retried");
        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Running_cancel_stays_refused_when_disabled(DatabaseKind kind)
    {
        await using WorkerTestHost host = await HostAsync(kind, enabled: false);
        JobId id = await host.EnqueueAsync(new P7bPayload("off"));
        ClaimedJob job = (await host.ClaimOneAsync(P7b.Step))!;
        (await CancelAsync(host, id)).Should().Be(CancelResult.RunningNotSupported);
        (await ExecuteAsync(host, job)).Should().Be(ExecutionResult.Succeeded);
    }
}