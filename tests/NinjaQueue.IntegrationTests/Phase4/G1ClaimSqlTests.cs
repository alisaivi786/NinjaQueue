using System.Diagnostics;
using Microsoft.Extensions.Options;
using NinjaQueue.Hosting;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// G1 (§16.1, §15.2, §17.6): the real claim, fence/renew, finalize and recovery SQL on PostgreSQL 16 and SQL Server 2022
/// with RCSI ON and OFF, executed against the server (no string comparisons, no mocks), plus the compatibility-level gate.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
[Trait("Gate", "G1")]
public sealed class G1ClaimSqlTests(JobsDatabaseFixture fixture)
{
    private const string Standard = "Test.P4.Standard.v1";
    private const string Other = "Test.P4.Other.v1";
    private const string Race = "Test.P4.Race.v1";

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Batch_claim_leases_charges_once_and_honours_the_capability_filter(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        IReadOnlyList<JobId> standard = await host.EnqueueManyAsync(Enumerable.Range(0, 6).Select(_ => new P4Payload("succeed")).ToList());
        IReadOnlyList<JobId> other = await host.EnqueueManyAsync(Enumerable.Range(0, 3).Select(i => new P4OtherPayload($"o{i}")).ToList());
        JobId future = await host.EnqueueAsync(new P4Payload("succeed"), new JobOptions { RunAt = DateTimeOffset.UtcNow.AddHours(1) });

        IReadOnlyList<ClaimedJob> first = await host.ClaimAsync([Standard], maxJobs: 4);

        first.Should().HaveCount(4);
        first.Select(j => j.LeaseToken).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        first.Should().OnlyContain(j => j.Attempt == 1 && j.AttemptSeq == 1 && j.MaxAttempts == 3 && j.JobType == Standard);
        foreach (ClaimedJob job in first)
        {
            object?[] row = (await host.Database.QueryAsync($"""
                SELECT q.lease_token, q.lease_owner, q.attempts_left, b.status, b.attempt,
                       CASE WHEN q.lease_expires_at_utc > {host.Database.Now} THEN 1 ELSE 0 END,
                       (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {host.Database.Bool(true)} AND a.lease_token = q.lease_token)
                  FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE q.job_id = {job.Id}
                """)).Single();
            ((Guid)row[0]!).Should().Be(job.LeaseToken, "the returned token is the one stored");
            row[1].Should().Be("p4-worker");
            Phase4Host.Int(row[2]).Should().Be(2, "attempts_left drops by exactly one");
            Phase4Host.Int(row[3]).Should().Be((int)JobStatus.Processing);
            Phase4Host.Int(row[4]).Should().Be(1);
            Phase4Host.Int(row[5]).Should().Be(1, "the lease is set from the database clock into the future");
            Phase4Host.Int(row[6]).Should().Be(1, "one charged attempt row carries the lease token");
        }

        (await host.ClaimAsync([Standard], maxJobs: 100)).Should().HaveCount(2, "the rest of the ready standard jobs, never a leased one again");
        (await host.ClaimAsync([Standard], maxJobs: 100)).Should().BeEmpty("the future job is not ready and the others are leased");
        (await host.QueueRowAsync(future)).Should().Be(((Guid?)null, 3));

        // The capability filter: a type the worker does not advertise is never claimed and charges nothing.
        foreach (JobId id in other)
        {
            (await host.StatusAsync(id)).Should().Be(JobStatus.Enqueued);
            (await host.QueueRowAsync(id)).Should().Be(((Guid?)null, 5));
            (await host.AttemptsAsync(id)).Should().Be((0, 0, 5));
        }

        (await host.ClaimAsync([Other], maxJobs: 10)).Select(j => j.JobId).Should().BeEquivalentTo(other);
        (await host.ClaimAsync([], maxJobs: 10)).Should().BeEmpty();
        standard.Should().HaveCount(6);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Keyed_rows_are_claimed_one_per_key_after_the_unkeyed_batch(DatabaseKind kind)
    {
        // Release 2 (§14.5): the unkeyed batch claim never takes a row carrying a concurrency_key; the keyed claim that follows
        // it takes at most one row per (tenant, key), and the second row of the key stays queued and uncharged.
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId keyed = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId sameKey = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId plain = await host.EnqueueAsync(new P4Payload("succeed"));
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.job_queue SET concurrency_key = 'k1' WHERE job_id IN ({await host.DbIdAsync(keyed)}, {await host.DbIdAsync(sameKey)})");

        (await host.ClaimAsync([Standard], maxJobs: 10)).Select(j => j.JobId).Should().Equal(plain, keyed);
        (await host.QueueRowAsync(sameKey)).Should().Be(((Guid?)null, 3));
        (await host.AttemptsAsync(sameKey)).Should().Be((0, 0, 3));
        (await host.ClaimAsync([Standard], maxJobs: 10)).Should().BeEmpty("k1 is running");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Stale_lease_token_renew_progress_outcome_and_fence_match_zero_rows(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId standardId = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId fencedId = await host.EnqueueAsync(new RacePayload(1));
        ClaimedJob job = (await host.ClaimOneAsync(Standard))!;
        ClaimedJob fenced = (await host.ClaimOneAsync(Race))!;
        ClaimedJob stale = job with { LeaseToken = Guid.NewGuid() };
        ClaimedJob staleFenced = fenced with { LeaseToken = Guid.NewGuid() };
        string expiresBefore = await LeaseExpiryTextAsync(host, job.Id);

        await host.InScopeAsync(async sp =>
        {
            IJobStore store = sp.GetRequiredService<IJobStore>();
            (await store.RenewAsync(stale, TimeSpan.FromMinutes(10), CancellationToken.None)).Matched.Should().BeFalse();
            (await store.ReportProgressAsync(stale, 1, 2, TimeSpan.FromMinutes(10), CancellationToken.None)).Should().BeFalse();
            (await store.RecordOutcomeAsync(stale, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)).Should().Be(OutcomeResult.LeaseLost);
            (await store.RecordOutcomeAsync(stale, new AttemptOutcome(AttemptOutcomeKind.Failed, TimeSpan.Zero, "x", "x"), CancellationToken.None)).Should().Be(OutcomeResult.LeaseLost);
            bool ran = false;
            FencedStepOutcome outcome = await store.RunFencedStepAsync(staleFenced, new FencedStepSettings(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20), true),
                (_, _) =>
                {
                    ran = true;
                    return Task.FromResult(new FencedStepResult(true, null, null, null));
                }, CancellationToken.None);
            outcome.Status.Should().Be(FencedStepStatus.LeaseLost);
            ran.Should().BeFalse("nothing runs behind a fence that matched 0 rows");
        });

        (await LeaseExpiryTextAsync(host, job.Id)).Should().Be(expiresBefore, "a stale renew changes nothing");
        (await host.StatusAsync(standardId)).Should().Be(JobStatus.Processing);
        (await host.StatusAsync(fencedId)).Should().Be(JobStatus.Processing);
        (await host.Database.ScalarAsync<int?>($"SELECT progress_processed FROM ninjaqueue.background_jobs WHERE id = {job.Id}")).Should().BeNull();

        // The holder of the real token finalizes; afterwards that token is stale too.
        await host.InScopeAsync(async sp =>
        {
            IJobStore store = sp.GetRequiredService<IJobStore>();
            (await store.RecordOutcomeAsync(job, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)).Should().Be(OutcomeResult.Recorded);
            (await store.RenewAsync(job, TimeSpan.FromMinutes(1), CancellationToken.None)).Matched.Should().BeFalse();
            (await store.RecordOutcomeAsync(job, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)).Should().Be(OutcomeResult.LeaseLost);
        });
        (await host.ExecuteAsync(fenced)).Should().Be(Execution.ExecutionResult.Succeeded);
        (await host.StatusAsync(standardId)).Should().Be(JobStatus.Succeeded);
        (await host.EventsAsync(standardId)).Count(e => e == JobEventCodes.JobSucceeded).Should().Be(1);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Renew_extends_the_lease_from_the_database_clock_and_reports_a_committed_cancel(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed"));
        ClaimedJob job = (await host.ClaimOneAsync(Standard, lease: TimeSpan.FromMinutes(1)))!;

        FenceResult renewed = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RenewAsync(job, TimeSpan.FromMinutes(10), CancellationToken.None));

        renewed.Matched.Should().BeTrue();
        renewed.CancelRequestedAtUtc.Should().BeNull();
        renewed.LeaseExpiresAtUtc.Should().NotBeNull();
        // The stored expiry is exactly what the statement returned, and it is about ten minutes past the database clock.
        double storedMinusReturnedMs = await host.Database.ScalarAsync<double>(host.Database.IsPostgres
            ? $"SELECT EXTRACT(EPOCH FROM (lease_expires_at_utc - '{renewed.LeaseExpiresAtUtc:O}'::timestamptz)) * 1000 FROM ninjaqueue.job_queue WHERE job_id = {job.Id}"
            : $"SELECT CAST(DATEDIFF_BIG(microsecond, CAST('{renewed.LeaseExpiresAtUtc:O}' AS datetimeoffset), lease_expires_at_utc) AS float) / 1000 FROM ninjaqueue.job_queue WHERE job_id = {job.Id}");
        Math.Abs(storedMinusReturnedMs).Should().BeLessThan(1);
        double secondsAheadOfDbClock = await host.Database.ScalarAsync<double>(host.Database.IsPostgres
            ? $"SELECT EXTRACT(EPOCH FROM (lease_expires_at_utc - now())) FROM ninjaqueue.job_queue WHERE job_id = {job.Id}"
            : $"SELECT CAST(DATEDIFF_BIG(millisecond, SYSDATETIMEOFFSET(), lease_expires_at_utc) AS float) / 1000 FROM ninjaqueue.job_queue WHERE job_id = {job.Id}");
        secondsAheadOfDbClock.Should().BeInRange(590, 600.5);

        // A cancel committed before the fence is seen by it (RCSI ON included, §16.1).
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'g1' WHERE id = {job.Id}");
        FenceResult withCancel = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RenewAsync(job, TimeSpan.FromMinutes(1), CancellationToken.None));
        withCancel.Matched.Should().BeTrue();
        withCancel.CancelRequestedAtUtc.Should().NotBeNull();
        (await host.StatusAsync(id)).Should().Be(JobStatus.Processing);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_fenced_row_is_skipped_by_a_concurrent_claim_and_by_recovery(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new RacePayload(1));
        ClaimedJob job = (await host.ClaimOneAsync(Race, lease: TimeSpan.FromMilliseconds(200)))!;
        int claimedMeanwhile = -1, recoveredMeanwhile = -1;
        TimeSpan probeDuration = TimeSpan.Zero;

        FencedStepOutcome outcome = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RunFencedStepAsync(job,
            new FencedStepSettings(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(20), true), async (_, ct) =>
            {
                await sp.GetRequiredService<WidgetProvider>().AddAsync($"{id.Value:N}-start", ct);
                await Task.Delay(600, ct); // the lease is now expired by the database clock, but this transaction holds the row

                // The concurrent claim and recovery run outside this step's execution context (their own connections).
                Task<(int, int, TimeSpan)> concurrent;
                using (ExecutionContext.SuppressFlow())
                {
                    concurrent = Task.Run(async () =>
                    {
                        Stopwatch watch = Stopwatch.StartNew();
                        int claimed = (await host.ClaimAsync([Race], maxJobs: 10)).Count;
                        int recovered = await host.RecoverAsync();
                        return (claimed, recovered, watch.Elapsed);
                    });
                }

                (claimedMeanwhile, recoveredMeanwhile, probeDuration) = await concurrent;
                return new FencedStepResult(true, null, null, null);
            }, CancellationToken.None));

        claimedMeanwhile.Should().Be(0);
        recoveredMeanwhile.Should().Be(0, "recovery skips a row held by an open fenced transaction");
        probeDuration.Should().BeLessThan(TimeSpan.FromSeconds(5), "claim and recovery skip the locked row instead of waiting on it");
        outcome.Status.Should().Be(FencedStepStatus.Completed);
        (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        (await host.WidgetsForAsync(id)).Should().Be(1);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Expired_lease_recovery_reschedules_cancels_and_fences_out_the_old_owner(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new P4Payload("succeed"));
        JobId cancelled = await host.EnqueueAsync(new P4OtherPayload("c"));
        // Leases long enough that a loaded machine cannot expire them before the first recovery pass.
        ClaimedJob job = (await host.ClaimOneAsync(Standard, lease: TimeSpan.FromSeconds(2)))!;
        ClaimedJob other = (await host.ClaimOneAsync(Other, lease: TimeSpan.FromSeconds(2)))!;
        await host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET cancel_requested_at_utc = {host.Database.Now}, cancel_reason = 'g1' WHERE id = {other.Id}");
        (await host.RecoverAsync()).Should().Be(0, "nothing has expired yet");
        await Task.Delay(2500);

        (await host.RecoverAsync()).Should().Be(2);

        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled);
        (await host.QueueRowAsync(id)).Should().Be(((Guid?)null, 2), "rescheduled unleased with the charged attempt kept");
        (await host.AttemptsAsync(id)).Should().Be((1, 1, 3));
        (await host.AttemptRowsAsync(id)).Should().Equal((1, (short)JobAttemptOutcome.LeaseLost, true));
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.JobLeaseExpired);
        (await host.StatusAsync(cancelled)).Should().Be(JobStatus.Cancelled, "a pending cancel wins at recovery");
        (await host.QueueRowAsync(cancelled)).Should().BeNull();

        await host.InScopeAsync(async sp =>
        {
            IJobStore store = sp.GetRequiredService<IJobStore>();
            (await store.RecordOutcomeAsync(job, new AttemptOutcome(AttemptOutcomeKind.Succeeded), CancellationToken.None)).Should().Be(OutcomeResult.LeaseLost);
            (await store.RenewAsync(job, TimeSpan.FromMinutes(1), CancellationToken.None)).Matched.Should().BeFalse();
        });

        await host.WaitUntilAvailableAsync(id);
        ClaimedJob again = (await host.ClaimOneAsync(Standard))!;
        again.LeaseToken.Should().NotBe(job.LeaseToken);
        again.Attempt.Should().Be(2);
        (await host.ExecuteAsync(again)).Should().Be(Execution.ExecutionResult.Succeeded);
        (await host.AttemptsAsync(id)).Should().Be((2, 2, 3));
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Claim_fence_and_finalize_run_at_the_minimum_supported_server_level(DatabaseKind kind)
    {
        // SQL Server: a dedicated database at compatibility level 130, the documented minimum (§17.6). PostgreSQL: the 16 server.
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind, kind == DatabaseKind.Postgres ? null : 130);
        await using Phase4Host host = Phase4Host.Build(db);
        if (db.IsPostgres)
        {
            (await db.ScalarAsync<int>("SELECT current_setting('server_version_num')::int")).Should().BeGreaterThanOrEqualTo(StartupChecks.MinPostgresVersionNum);
        }
        else
        {
            (await db.ScalarAsync<int>("SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()")).Should().Be(130);
            (await db.ScalarAsync<int>("SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME()"))
                .Should().Be(kind == DatabaseKind.SqlServerRcsiOn ? 1 : 0);
        }

        ServerCheck check = await StartupChecks.RunAsync(host.Get<IServiceScopeFactory>(), host.Get<JobHandlerRegistry>(), host.Get<IOptions<NinjaQueueOptions>>().Value, CancellationToken.None);
        check.ReadCommittedSnapshot.Should().Be(kind == DatabaseKind.SqlServerRcsiOn);

        await host.EnqueueManyAsync(Enumerable.Range(0, 3).Select(_ => new P4Payload("succeed")).ToList());
        JobId fencedId = await host.EnqueueAsync(new RacePayload(2));
        IReadOnlyList<ClaimedJob> claimed = await host.ClaimAsync([Standard, Other], maxJobs: 2);
        claimed.Should().HaveCount(2);
        claimed.Select(j => j.LeaseToken).Should().OnlyHaveUniqueItems();
        foreach (ClaimedJob job in claimed)
        {
            (await host.Database.ScalarAsync<int>($"SELECT attempts_left FROM ninjaqueue.job_queue WHERE job_id = {job.Id}")).Should().Be(2);
            (await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RenewAsync(job, TimeSpan.FromMinutes(2), CancellationToken.None))).Matched.Should().BeTrue();
            (await host.ExecuteAsync(job)).Should().Be(Execution.ExecutionResult.Succeeded);
        }

        (await host.ClaimAsync([Standard], maxJobs: 10)).Should().ContainSingle();
        (await host.RunOneAsync(Race)).Should().Be(Execution.ExecutionResult.Succeeded, "the fence, finalization and completion SQL run at this level too");
        (await host.WidgetsForAsync(fencedId)).Should().Be(2);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Startup_refuses_a_database_below_the_minimum_server_level(DatabaseKind kind)
    {
        NinjaQueueOptions options = new();
        if (kind == DatabaseKind.Postgres)
        {
            // A PostgreSQL 12 server is not available in CI; the refusal rule is the same code path the host start runs.
            FluentActions.Invoking(() => StartupChecks.Validate(new ServerCheck(true, 120_022, false, true), hasFencedJobs: true, options))
                .Should().Throw<InvalidOperationException>().WithMessage("*PostgreSQL 13*");
            await using Phase4Host pg = await Phase4Host.StartAsync(fixture, kind);
            await FluentActions.Awaiting(() => pg.StartWorkersAsync()).Should().NotThrowAsync("PostgreSQL 16 passes the check");
            return;
        }

        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind, 120);
        (await db.ScalarAsync<int>("SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()")).Should().Be(120);
        await using Phase4Host host = Phase4Host.Build(db);

        // Driven through the real host start: the worker host refuses before any loop starts.
        (await FluentActions.Awaiting(() => host.Get<NinjaQueueWorkerHost>().StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*compatibility level 130*COMPATIBILITY_LEVEL = 130*");

        // Why: the claim's and the set-based enqueue's OPENJSON does not exist below level 130.
        await FluentActions.Awaiting(() => host.EnqueueAsync(new P4Payload("succeed"))).Should().ThrowAsync<SqlException>();
        await FluentActions.Awaiting(() => host.ClaimAsync([Standard])).Should().ThrowAsync<SqlException>();
    }

    private static async Task<string> LeaseExpiryTextAsync(Phase4Host host, long jobId)
        => (await host.Database.QueryAsync($"SELECT lease_expires_at_utc FROM ninjaqueue.job_queue WHERE job_id = {jobId}")).Single()[0]!.ToString()!;
}