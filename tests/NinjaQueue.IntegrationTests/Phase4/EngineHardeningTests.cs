using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.Execution;
using NinjaQueue.Hosting;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase4;

public sealed record LongRetryPayload(string Note);

/// <summary>Asks for a retry 60 days out: longer than an int of milliseconds (~24.8 days).</summary>
[JobType("Test.P4.LongRetry.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 3)]
public sealed class LongRetryHandler : IJobHandler<LongRetryPayload>
{
    public static readonly TimeSpan Delay = TimeSpan.FromDays(60);

    public Task<JobResult> HandleAsync(LongRetryPayload payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.RetryAfter(Delay, "Come back in two months."));
}

/// <summary>
/// Phase 4 hardening: engine failures inside the fenced transaction end the attempt, long delays do not overflow SQL Server's
/// DATEADD, the job-age ceiling uses the database clock, and a hanging lease renewal self-fences before the lease expires.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
public sealed class EngineHardeningTests(JobsDatabaseFixture fixture)
{
    private const string Race = "Test.P4.Race.v1";
    private const string Standard = "Test.P4.Standard.v1";
    private const string LongRetry = "Test.P4.LongRetry.v1";

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_non_database_failure_inside_the_fenced_transaction_outside_the_handler_rolls_back_and_retries(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new RacePayload(1));

        ExecutionResult result;
        FencedStepTestHooks.BeforeCommit.Value = () => throw new InvalidOperationException("Injected engine failure after finalization, before COMMIT.");
        try
        {
            result = (await host.RunOneAsync(Race))!.Value;
        }
        finally
        {
            FencedStepTestHooks.BeforeCommit.Value = null;
        }

        result.Should().Be(ExecutionResult.Rescheduled, "the attempt is recorded as a failure instead of escaping the executor");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        (await host.QueueRowAsync(id)).Should().NotBeNull().And.Match<(Guid? LeaseToken, int AttemptsLeft)?>(q => q!.Value.LeaseToken == null,
            "the lease is cleared at once, not left to expire");
        (await host.WidgetsForAsync(id)).Should().Be(0, "the step rolled back with its finalization");
        (await host.AttemptRowsAsync(id)).Should().Equal((1, (short?)JobAttemptOutcome.Failed, true));
        (await host.EventsAsync(id)).Should().EndWith(JobEventCodes.JobRetryScheduled).And.NotContain(JobEventCodes.JobSucceeded);

        (await host.RunUntilTerminalAsync(id, Race)).Should().Be(JobStatus.Succeeded, "the retry runs normally");
        (await host.WidgetsForAsync(id)).Should().Be(1);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_retry_delay_of_60_days_is_scheduled_without_overflow(DatabaseKind kind)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        await using Phase4Host host = Phase4Host.Build(db, extraHandlers: b => b.AddJobHandler<LongRetryHandler>());
        JobId id = await host.EnqueueAsync(new LongRetryPayload("later"));

        (await host.RunOneAsync(LongRetry)).Should().Be(ExecutionResult.Rescheduled);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        string secondsUntil = db.IsPostgres
            ? "EXTRACT(EPOCH FROM (q.available_at_utc - now()))"
            : "DATEDIFF_BIG(second, SYSDATETIMEOFFSET(), q.available_at_utc)";
        double seconds = await db.ScalarAsync<double>($"""
            SELECT {secondsUntil} FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.guid_id = '{id.Value}'
            """);
        seconds.Should().BeInRange(LongRetryHandler.Delay.TotalSeconds - 120, LongRetryHandler.Delay.TotalSeconds + 1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task MaxJobAge_is_measured_from_the_database_stamped_creation_time(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind, o => o.Retry.MaxJobAge = TimeSpan.FromHours(1));
        JobId young = await host.EnqueueAsync(new P4Payload("fail"));
        JobId old = await host.EnqueueAsync(new P4Payload("fail"));
        await host.Database.ExecuteAsync(host.Database.IsPostgres
            ? $"UPDATE ninjaqueue.background_jobs SET created_at_utc = now() - interval '59 minutes' WHERE guid_id = '{young.Value}'"
            : $"UPDATE ninjaqueue.background_jobs SET created_at_utc = DATEADD(minute, -59, SYSDATETIMEOFFSET()) WHERE guid_id = '{young.Value}'");
        await host.Database.ExecuteAsync(host.Database.IsPostgres
            ? $"UPDATE ninjaqueue.background_jobs SET created_at_utc = now() - interval '61 minutes' WHERE guid_id = '{old.Value}'"
            : $"UPDATE ninjaqueue.background_jobs SET created_at_utc = DATEADD(minute, -61, SYSDATETIMEOFFSET()) WHERE guid_id = '{old.Value}'");

        (await host.RunOneAsync(Standard)).Should().NotBeNull();
        (await host.RunOneAsync(Standard)).Should().NotBeNull();

        (await host.StatusAsync(young)).Should().Be(JobStatus.Scheduled, "59 minutes is under the 1-hour ceiling");
        (await host.StatusAsync(old)).Should().Be(JobStatus.DeadLettered);
        (await host.TerminalReasonAsync(old)).Should().Be((short)JobTerminalReason.MaxAgeExceeded);
    }

    /// <summary>
    /// The database clock, not the app clock, decides the age. PostgreSQL resolves an unqualified <c>now()</c> through the
    /// search path when <c>pg_catalog</c> is listed explicitly after another schema, so a shadow <c>now()</c> skews the
    /// database clock the engine sees by ±10 days while the app clock stays real (SQL Server has no equivalent).
    /// </summary>
    [Theory]
    [InlineData(10, 1, JobStatus.DeadLettered)] // DB clock 10 days ahead, created 1 real day ago: 11 days old by the DB.
    [InlineData(-10, 8, JobStatus.Scheduled)]   // DB clock 10 days behind, created 8 real days ago: not yet created by the DB.
    public async Task MaxJobAge_ignores_the_app_clock_when_the_database_clock_differs(int dbClockOffsetDays, int createdRealDaysAgo, JobStatus expected)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        await db.ExecuteAsync($"""
            CREATE SCHEMA fake_clock;
            CREATE FUNCTION fake_clock.now() RETURNS timestamptz LANGUAGE sql STABLE AS $$ SELECT pg_catalog.now() + interval '{dbClockOffsetDays} days' $$;
            """);
        TestDatabase skewed = db with
        {
            ConnectionString = new NpgsqlConnectionStringBuilder(db.ConnectionString) { SearchPath = "fake_clock,pg_catalog,public" }.ConnectionString,
        };
        await using Phase4Host host = Phase4Host.Build(skewed);
        JobId id = await host.EnqueueAsync(new P4Payload("fail"));
        await db.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET created_at_utc = pg_catalog.now() - interval '{createdRealDaysAgo} days' WHERE guid_id = '{id.Value}'");

        (await host.RunOneAsync(Standard)).Should().NotBeNull();

        (await host.StatusAsync(id)).Should().Be(expected);
        if (expected == JobStatus.DeadLettered)
        {
            (await host.TerminalReasonAsync(id)).Should().Be((short)JobTerminalReason.MaxAgeExceeded);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hanging_renewal_self_fences_before_the_lease_expires(bool honoursCancellation)
    {
        NinjaQueueOptions options = new();
        options.Worker.LeaseDuration = TimeSpan.FromSeconds(12);
        options.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(3);
        HangingRenewStore store = new(honoursCancellation);
        ServiceCollection services = new();
        services.AddScoped<IJobStore>(_ => store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        LeaseRenewer renewer = new(provider.GetRequiredService<IServiceScopeFactory>(), options, NullLogger.Instance);
        ClaimedJob job = new(1, new JobId(Guid.NewGuid()), Standard, null, Guid.NewGuid(), "w", DateTimeOffset.UtcNow.AddSeconds(12), 1, 1, 3,
            "{}", null, null, null, 0, 0, DateTimeOffset.UtcNow);

        using JobSignals signals = new(CancellationToken.None);
        using CancellationTokenSource attemptEnded = new();
        TaskCompletionSource<TimeSpan> leaseLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Stopwatch clock = Stopwatch.StartNew();
        using CancellationTokenRegistration _ = signals.Token.Register(() => leaseLost.TrySetResult(clock.Elapsed));
        Task renewing = renewer.RunAsync(job, signals, attemptEnded.Token);

        TimeSpan firedAt = await leaseLost.Task.WaitAsync(TimeSpan.FromSeconds(20));
        signals.LeaseLost.Should().BeTrue();
        firedAt.Should().BeLessThan(options.Worker.LeaseDuration - TimeSpan.FromSeconds(1), "the worker stops before the database lease could expire");
        firedAt.Should().BeGreaterThan(TimeSpan.FromSeconds(5), "the first renewal starts after a third of the lease and is given until the self-fence deadline");
        store.Calls.Should().Be(1, "the single hanging call was cut off at the deadline, not retried past it");
        await renewing.WaitAsync(TimeSpan.FromSeconds(5));
        await attemptEnded.CancelAsync();
        store.Release();
    }

    /// <summary>An <see cref="IJobStore"/> whose renewal never answers, standing in for a paused or partitioned server.</summary>
    private sealed class HangingRenewStore(bool honoursCancellation) : IJobStore
    {
        private readonly TaskCompletionSource<FenceResult> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _never.TrySetResult(FenceResult.Lost);

        public Task<FenceResult> RenewAsync(ClaimedJob job, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return honoursCancellation ? _never.Task.WaitAsync(cancellationToken) : _never.Task;
        }

        public Task<JobId> StageAsync(NewJob job, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<JobId>> EnqueueAsync(IReadOnlyList<NewJob> jobs, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string queue, IReadOnlyList<string> jobTypes, string workerId, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<FencedStepOutcome> RunFencedStepAsync(ClaimedJob job, FencedStepSettings settings, Func<string?, CancellationToken, Task<FencedStepResult>> step, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ReportProgressAsync(ClaimedJob job, int processed, int? total, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OutcomeResult> RecordOutcomeAsync(ClaimedJob job, AttemptOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}