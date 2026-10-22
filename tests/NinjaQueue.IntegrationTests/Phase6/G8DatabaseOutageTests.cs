using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase6;

/// <summary>
/// Gate G8 (§16.5, §14.2, §7.2): the database server is paused (<c>docker pause</c>, dedicated containers) while a standard job
/// renews its lease, a fenced step holds its transaction and the queue loop claims. The standard worker self-fences before its
/// lease could have expired, the fenced step is cancelled and rolls back, claims fail without killing the worker and health
/// leaves Healthy. After the unpause both jobs are recovered and reach Succeeded with the outage charged, the fenced step's
/// write exists exactly once, and health returns to Healthy.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G8")]
public sealed class G8DatabaseOutageTests(PausableDatabaseFixture outage) : IClassFixture<PausableDatabaseFixture>
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(12);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Paused_database_self_fences_rolls_back_survives_and_recovers(DatabaseKind kind)
    {
        TestDatabase db = await outage.CreateDatabaseAsync(kind);
        await WorkerTestHost.PrepareAsync(db);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>().AddJobHandler<LongFencedProbeHandler>(), o =>
        {
            o.Worker.LeaseDuration = Lease;
            o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(3);
            o.Worker.MaintenanceInterval = TimeSpan.FromSeconds(1);
        });
        long standardCancelled = 0;
        long fencedCancelled = 0;
        host.Get<ProbePlan>().Behaviour = async (_, context, ct) =>
        {
            if (context.Attempt == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref standardCancelled, Stopwatch.GetTimestamp());
                    throw;
                }
            }

            return JobResult.Success();
        };
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref fencedCancelled, Stopwatch.GetTimestamp());
                    throw;
                }
            }

            return Step.Complete();
        };

        await host.StartAsync();
        JobId standard = await host.EnqueueAsync(new ProbePayload("std"));
        JobId fenced = await host.EnqueueAsync(new LongFencedPayload("fenced"));
        await Task.WhenAll(host.Get<ProbePlan>().Started("std"), host.Get<FencedProbePlan>().Started("fenced")).WaitAsync(TimeSpan.FromSeconds(20));
        (await host.HealthAsync()).Status.Should().Be(HealthStatus.Healthy);

        List<HealthStatus> duringOutage = [];
        long pausedAt = Stopwatch.GetTimestamp();
        await outage.PauseAsync(kind);
        try
        {
            (await JobProbes.EventuallyAsync(() => Task.FromResult(host.Worker.ClaimsFailing), TimeSpan.FromSeconds(30)))
                .Should().BeTrue("claims fail while the server is paused; the worker backs off instead of dying");
            using (CancellationTokenSource healthTimeout = new(TimeSpan.FromSeconds(20)))
            {
                duringOutage.Add((await host.HealthAsync(healthTimeout.Token)).Status);
            }

            (await JobProbes.EventuallyAsync(() => Task.FromResult(Interlocked.Read(ref standardCancelled) != 0 && Interlocked.Read(ref fencedCancelled) != 0), TimeSpan.FromSeconds(30)))
                .Should().BeTrue("the standard worker self-fences and the fenced step is cancelled while ownership cannot be confirmed");
            Stopwatch.GetElapsedTime(pausedAt, Interlocked.Read(ref standardCancelled)).Should().BeLessThan(Lease,
                "the worker self-fences before its last confirmed lease could have expired (§14.2)");
        }
        finally
        {
            await outage.UnpauseAsync(kind);
        }

        // Back online: recovery owns both jobs; each reaches a correct terminal state.
        (await JobProbes.EventuallyAsync(async () => await db.StatusOfAsync(standard) == JobStatus.Succeeded && await db.StatusOfAsync(fenced) == JobStatus.Succeeded,
            TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(500))).Should().BeTrue("standard: {0} [{1}]; fenced: {2} [{3}] {4}; blocking: {5}; logs: {6}",
            await db.StatusOfAsync(standard), await db.AttemptTrailAsync(standard), await db.StatusOfAsync(fenced), await db.AttemptTrailAsync(fenced),
            string.Join(",", await db.EventsOfAsync(fenced)), await db.BlockingAsync(), host.Logs);
        (await db.WidgetCountAsync("fenced")).Should().Be(1, "the outage step rolled back; only the successful attempt's write exists");
        // The outage attempt is charged; right after an unpause a slow first renewal can self-fence once more (also charged).
        // What must hold is the accounting: attempt = charged rows, bounded by max_attempts, and exactly one success.
        foreach (JobId id in new[] { standard, fenced })
        {
            (int attempt, int charged, int max) = await db.AttemptsOfAsync(id);
            string trail = await db.AttemptTrailAsync(id);
            attempt.Should().Be(charged, trail);
            attempt.Should().BeInRange(2, max, "the outage attempt was charged and the job still succeeded within its budget ({0})", trail);
            (await db.EventsOfAsync(id)).Count(e => e == JobEventCodes.JobSucceeded).Should().Be(1, trail);
        }

        (await db.EventsOfAsync(standard)).Should().Contain(JobEventCodes.JobLeaseExpired, "the self-fenced worker wrote nothing; recovery took the job back");
        (await db.EventsOfAsync(fenced)).Should().Contain(e => e == JobEventCodes.JobLeaseExpired || e == JobEventCodes.JobTimedOut);

        duringOutage.Should().OnlyContain(s => s != HealthStatus.Healthy, "health leaves Healthy during the outage");
        (await JobProbes.EventuallyAsync(async () => (await host.HealthAsync()).Status == HealthStatus.Healthy, TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(500)))
            .Should().BeTrue("health returns to Healthy once claims succeed again");
        host.Worker.ClaimsFailing.Should().BeFalse();
    }

    /// <summary>
    /// G8, outcome recording: the handler itself pauses the server as its last act (deterministic: after the work, before any
    /// outcome or finalization write reaches the database). The outcome is then either recorded exactly once, or not at all and
    /// the job is recovered after its lease expires; the attempt count matches the charged attempt rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Outage_between_a_standard_handlers_return_and_its_outcome_record_is_recorded_once_or_recovered(DatabaseKind kind)
    {
        TestDatabase db = await outage.CreateDatabaseAsync(kind);
        await WorkerTestHost.PrepareAsync(db);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.Enabled = false);
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<ProbePlan>().Behaviour = async (_, context, _) =>
        {
            if (context.Attempt == 1)
            {
                await outage.PauseAsync(kind);
                paused.TrySetResult();
            }

            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new ProbePayload("record"));
        ClaimedJob job = (await host.ClaimOneAsync("Test.Probe.v1", TimeSpan.FromSeconds(3)))!;

        JobStatus? afterOutage = await RunAcrossOutageAsync(kind, db, id, paused, () => ExecuteAsync(host, job));

        (await host.DriveToTerminalAsync(id, "Test.Probe.v1", TimeSpan.FromSeconds(90))).Should().BeTrue();
        await AssertRecordedOnceAsync(db, id, afterOutage);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Outage_between_a_fenced_step_and_its_finalization_never_duplicates_the_business_write(DatabaseKind kind)
    {
        TestDatabase db = await outage.CreateDatabaseAsync(kind);
        await WorkerTestHost.PrepareAsync(db);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o =>
        {
            o.Worker.Enabled = false;
            o.Worker.LeaseDuration = TimeSpan.FromSeconds(6);
            o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(2);
        });
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                await outage.PauseAsync(kind); // after the business write, before SaveChanges, the finalization and COMMIT
                paused.TrySetResult();
            }

            return Step.Complete();
        };
        JobId id = await host.EnqueueAsync(new FencedProbePayload("finalize"));
        ClaimedJob job = (await host.ClaimOneAsync("Test.Fenced.Probe.v1"))!;

        JobStatus? afterOutage = await RunAcrossOutageAsync(kind, db, id, paused, () => ExecuteAsync(host, job));

        (await host.DriveToTerminalAsync(id, "Test.Fenced.Probe.v1", TimeSpan.FromSeconds(90))).Should().BeTrue();
        await AssertRecordedOnceAsync(db, id, afterOutage);
        (await db.WidgetCountAsync("finalize")).Should().Be(1, "the business write committed exactly once: with the finalization that succeeded");
    }

    private static async Task<Execution.ExecutionResult> ExecuteAsync(WorkerTestHost host, ClaimedJob job)
    {
        using Execution.JobSignals signals = new(CancellationToken.None);
        return await host.Get<Execution.JobExecutor>().ExecuteAsync(job, signals);
    }

    /// <summary>Runs the attempt; once the handler has paused the server, keeps it down long enough for every client timeout to
    /// fire, then unpauses and returns the job's status as left by the outage (before any recovery).</summary>
    private async Task<JobStatus?> RunAcrossOutageAsync(DatabaseKind kind, TestDatabase db, JobId id, TaskCompletionSource paused, Func<Task<Execution.ExecutionResult>> run)
    {
        Task<Execution.ExecutionResult> attempt = Task.Run(run);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await outage.UnpauseAsync(kind);
        }

        try
        {
            await attempt.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // The outcome write failed with the server down: recovery owns the job.
        }

        return await db.StatusOfAsync(id);
    }

    private static async Task AssertRecordedOnceAsync(TestDatabase db, JobId id, JobStatus? afterOutage)
    {
        List<string> events = await db.EventsOfAsync(id);
        string trail = $"after outage: {afterOutage}; events: {string.Join(", ", events)}; attempts: {await db.AttemptTrailAsync(id)}";
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Succeeded, trail);
        events.Count(e => e == JobEventCodes.JobSucceeded).Should().Be(1, "the outcome is recorded exactly once ({0})", trail);
        (int attempt, int charged, int max) = await db.AttemptsOfAsync(id);
        attempt.Should().Be(charged, "attempt equals the charged attempt rows ({0})", trail);
        max.Should().Be(3);
        switch (afterOutage)
        {
            case JobStatus.Succeeded:
                attempt.Should().Be(1, "recorded at the edge of the outage: no retry was needed ({0})", trail);
                break;
            case JobStatus.Scheduled:
                // The failed attempt's outcome reached the database once it was back: recorded once, charged, retried.
                attempt.Should().Be(2, "the failed attempt was recorded once and charged ({0})", trail);
                events.Count(e => e is JobEventCodes.JobTimedOut or JobEventCodes.JobRetryScheduled).Should().Be(1, trail);
                break;
            default:
                afterOutage.Should().Be(JobStatus.Processing, "nothing was recorded while the server was down ({0})", trail);
                attempt.Should().Be(2, "the lost attempt is charged, the recovered retry succeeded ({0})", trail);
                events.Should().Contain(JobEventCodes.JobLeaseExpired, trail);
                break;
        }
    }
}