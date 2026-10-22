using System.Net;
using System.Text.Json;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.Execution;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Phase 5 (§7.3, §14.1, §14.3 rule 7): backoff with full jitter, the classifier, retries bounded by <c>max_attempts</c>,
/// dead-letter of permanent errors and invalid payloads, snooze and release refunds with their caps, and <c>MaxJobAge</c>.
/// Every state is verified from a second connection, on PostgreSQL and SQL Server RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "5")]
public sealed class RetryTests(JobsDatabaseFixture fixture)
{
    private const string Probe = "Test.Probe.v1";

    private Task<JobsHost> HostAsync(DatabaseKind kind, Action<NinjaQueueOptions>? configure = null)
        => JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            b.Services.AddSingleton<ProbePlan>();
        }, configure);

    [Fact]
    public void Backoff_is_full_jitter_under_the_exponential_ceiling()
    {
        RetryBackoff backoff = new(TimeSpan.FromSeconds(15), TimeSpan.FromHours(1));
        for (int attempt = 1; attempt <= 12; attempt++)
        {
            TimeSpan ceiling = TimeSpan.FromMilliseconds(Math.Min(TimeSpan.FromHours(1).TotalMilliseconds, 15_000 * Math.Pow(2, attempt - 1)));
            TimeSpan[] samples = [.. Enumerable.Range(0, 2000).Select(_ => backoff.Next(attempt))];

            samples.Should().OnlyContain(d => d >= TimeSpan.Zero && d < ceiling, $"attempt {attempt}: random(0, min(cap, base * 2^(attempt-1)))");
            samples.Min().Should().BeLessThan(ceiling * 0.1, "full jitter reaches near zero");
            samples.Max().Should().BeGreaterThan(ceiling * 0.9, "full jitter reaches near the ceiling");
        }

        backoff.Next(0).Should().BeLessThan(TimeSpan.FromSeconds(15), "attempt 0 is treated as the first attempt");
        backoff.Next(1000).Should().BeLessThan(TimeSpan.FromHours(1), "the cap holds for any attempt");
    }

    [Fact]
    public void Classifier_separates_permanent_from_transient_errors()
    {
        DefaultRetryClassifier classifier = new();

        classifier.IsPermanent(new PermanentJobException("x")).Should().BeTrue();
        classifier.IsPermanent(new JsonException("x")).Should().BeTrue();
        classifier.IsPermanent(new ArgumentException("x")).Should().BeTrue();
        classifier.IsPermanent(new FencedContractViolationException("x")).Should().BeTrue();
        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.NotFound)).Should().BeTrue();
        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.BadRequest)).Should().BeTrue();

        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)).Should().BeFalse();
        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.RequestTimeout)).Should().BeFalse();
        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.Conflict)).Should().BeFalse();
        classifier.IsPermanent(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)).Should().BeFalse();
        classifier.IsPermanent(new HttpRequestException("x")).Should().BeFalse();
        classifier.IsPermanent(new TimeoutException("x")).Should().BeFalse();
        classifier.IsPermanent(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Permanent_error_dead_letters_on_the_first_attempt(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        host.Get<ProbePlan>().Behaviour = (_, _, _) => throw new PermanentJobException("The referenced record does not exist.");
        JobId id = await host.EnqueueAsync(new ProbePayload("permanent"));

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.DeadLettered);

        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.CountQueueAsync(id)).Should().Be(0);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "no retry is spent on a permanent error");
        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.PermanentError);
        (await host.Database.JobColumnAsync<string>(id, "last_error_code")).Should().Be("Permanent");
        (await host.EventsAsync(id)).Should().Equal(JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobDeadLettered);
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Failed);
        (await host.RunOneAsync(Probe)).Should().BeNull("a dead-lettered job is never claimed again");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Handler_throw_retries_until_max_attempts_then_dead_letters(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        ProbePlan plan = host.Get<ProbePlan>();
        plan.Behaviour = (_, _, _) => throw new InvalidOperationException("Transient failure.");
        JobId id = await host.EnqueueAsync(new ProbePayload("flaky"));

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.Rescheduled);
            (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
            (await host.AttemptsAsync(id)).Should().Be((attempt, attempt, 3));
            (await host.Database.QueueStateAsync(id)).Should().Be((false, false, 3 - attempt), "the lease is cleared and one attempt fewer is left");
            await host.Database.MakeAvailableNowAsync(id);
        }

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.DeadLettered);

        plan.Calls.Should().Be(3);
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(id)).Should().Be((3, 3, 3), "attempt never exceeds max_attempts");
        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.Exhausted);
        (await host.CountQueueAsync(id)).Should().Be(0);
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Failed, JobAttemptOutcome.Failed, JobAttemptOutcome.Failed);
        (await host.EventsAsync(id)).Should().Equal(
            JobEventCodes.JobEnqueued, JobEventCodes.JobClaimed, JobEventCodes.JobRetryScheduled, JobEventCodes.JobClaimed, JobEventCodes.JobRetryScheduled,
            JobEventCodes.JobClaimed, JobEventCodes.JobDeadLettered);
        (await host.RunOneAsync(Probe)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Invalid_payload_for_a_known_code_dead_letters_and_keeps_the_payload(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        JobId id = await host.EnqueueAsync(new ProbePayload("payload"));
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET payload = '{{\"Name\": ' WHERE guid_id = '{id.Value}'");

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.DeadLettered);

        host.Get<ProbePlan>().Calls.Should().Be(0, "the handler never sees a payload it cannot read");
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.Database.JobColumnAsync<string>(id, "last_error_code")).Should().Be("InvalidPayload");
        (await host.Database.JobColumnAsync<string>(id, "payload")).Should().Be("{\"Name\": ", "the payload is kept for replay after a fix");
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Snooze_is_refunded_until_the_cap_then_counts_as_a_failure(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        host.Get<ProbePlan>().Behaviour = (_, _, _) => Task.FromResult(JobResult.Snooze(TimeSpan.FromSeconds(1)));
        JobId id = await host.EnqueueAsync(new ProbePayload("snooze"));

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.Rescheduled);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 3), "a snooze is refunded");
        (await host.Database.QueueStateAsync(id)).Should().Be((false, false, 3));
        (await host.Database.JobColumnAsync<int>(id, "snooze_count")).Should().Be(1);
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Snoozed);
        (await host.EventsAsync(id)).Should().EndWith(JobEventCodes.JobSnoozed);
        (await host.RunOneAsync(Probe)).Should().BeNull("a snooze of at least 1 s is honoured");

        // 50 snoozes already taken (MaxSnoozes): the 51st counts as a failure, charged, and the counter stays at the cap.
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET snooze_count = 50 WHERE guid_id = '{id.Value}'");
        await host.Database.MakeAvailableNowAsync(id);

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.Rescheduled);

        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "over the cap the snooze is charged");
        (await host.Database.JobColumnAsync<int>(id, "snooze_count")).Should().Be(50);
        (await host.Database.JobColumnAsync<string>(id, "last_error_code")).Should().Be("SnoozeLimit");
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Snoozed, JobAttemptOutcome.Failed);
        (await host.EventsAsync(id)).Should().EndWith(JobEventCodes.JobRetryScheduled);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Release_is_refunded_until_MaxReleases_then_charged(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        host.Get<ProbePlan>().Behaviour = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return JobResult.Success();
        };
        JobId id = await host.EnqueueAsync(new ProbePayload("release"));
        using CancellationTokenSource shutdown = new();
        await shutdown.CancelAsync();

        (await host.ExecuteAsync((await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!, shutdown.Token)).Should().Be(ExecutionResult.Rescheduled);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Enqueued, "a released job is available again at once");
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 3), "a shutdown release is refunded");
        (await host.Database.QueueStateAsync(id)).Should().Be((false, false, 3));
        (await host.Database.JobColumnAsync<int>(id, "release_count")).Should().Be(1);
        (await host.Database.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.Released);
        (await host.EventsAsync(id)).Should().EndWith(JobEventCodes.JobReleased);

        // MaxReleases (10) already used: the next release is charged.
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET release_count = 10 WHERE guid_id = '{id.Value}'");
        (await host.ExecuteAsync((await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!, shutdown.Token)).Should().Be(ExecutionResult.Rescheduled);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Enqueued);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "over MaxReleases a release costs an attempt");
        (await host.Database.QueueStateAsync(id)).Should().Be((false, false, 2));
        (await host.Database.JobColumnAsync<int>(id, "release_count")).Should().Be(11);

        // Over the cap with no attempt left: dead-lettered, never released forever.
        (await host.ExecuteAsync((await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!, shutdown.Token)).Should().Be(ExecutionResult.Rescheduled);
        (await host.ExecuteAsync((await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!, shutdown.Token)).Should().Be(ExecutionResult.DeadLettered);
        (await host.AttemptsAsync(id)).Should().Be((3, 3, 3));
        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.Exhausted);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Job_older_than_MaxJobAge_dead_letters_with_MaxAgeExceeded(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        host.Get<ProbePlan>().Behaviour = (_, _, _) => throw new InvalidOperationException("Transient failure.");
        JobId id = await host.EnqueueAsync(new ProbePayload("old"));
        await host.Database.ExecuteAsync(host.Database.IsPostgres
            ? $"UPDATE ninjaqueue.background_jobs SET created_at_utc = now() - interval '8 days' WHERE guid_id = '{id.Value}'"
            : $"UPDATE ninjaqueue.background_jobs SET created_at_utc = DATEADD(day, -8, SYSDATETIMEOFFSET()) WHERE guid_id = '{id.Value}'");

        (await host.RunOneAsync(Probe)).Should().Be(ExecutionResult.DeadLettered, "a retry is not scheduled past MaxJobAge (7 days)");

        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "attempts were left, the age ceiling wins regardless");
        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.MaxAgeExceeded);
        (await host.EventsAsync(id)).Should().EndWith(JobEventCodes.JobMaxAgeExceeded);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Tenant_mismatch_is_a_permanent_failure(DatabaseKind kind)
    {
        Guid tenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a5");
        Guid tenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b5");
        await using JobsHost host = await JobsHost.StartAsync(fixture, kind, b => b.AddJobHandler<CreateWidgetsHandler>());
        JobId id = await host.EnqueueAsync(new WidgetPayload(tenantB, "mismatch"), new JobOptions { TenantId = tenantA });

        (await host.RunOneAsync("Test.Widgets.Create.v1")).Should().Be(ExecutionResult.DeadLettered);

        (await host.CountWidgetsAsync("mismatch")).Should().Be(0, "the step stopped at EnsureTenant before any write");
        (await host.StatusAsync(id)).Should().Be(JobStatus.DeadLettered);
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3), "a tenant mismatch is not retried");
        (await host.Database.JobColumnAsync<short>(id, "terminal_reason_code")).Should().Be((short)JobTerminalReason.PermanentError);
        (await host.Database.ScalarAsync<string>($"""
            SELECT a.exception_type FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE b.guid_id = '{id.Value}'
            """)).Should().Be(typeof(PermanentJobException).FullName);
    }
}