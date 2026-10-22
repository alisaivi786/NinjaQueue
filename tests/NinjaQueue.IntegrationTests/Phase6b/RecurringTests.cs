using System.Collections.Concurrent;
using NinjaQueue.Registry;
using NinjaQueue.Storage;
using static NinjaQueue.IntegrationTests.Phase6b.Phase6bSupport;

namespace NinjaQueue.IntegrationTests.Phase6b;

/// <summary>
/// Phase 6b recurring schedules (§14.6, §15.8, §15.13) on PostgreSQL 16 and SQL Server 2022 with RCSI ON and OFF: the startup
/// sync (versions, removal, admin pause), the misfire policies, DST through real database timestamps, one enqueue per
/// occurrence across instances, and the atomic overlap skip.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "6b")]
public sealed class RecurringTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Sync_inserts_respects_versions_disables_removed_and_keeps_an_admin_pause(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using (WorkerTestHost v1 = Host(db, b => b
            .AddRecurring("sync.a", "0 2 * * *", "Asia/Karachi", new ProbePayload("a"))
            .AddRecurring("sync.b", "0 3 * * *", "UTC", new ProbePayload("b"))))
        {
            (await v1.SyncAsync()).Should().Be(new RecurringSyncResult(2, 0, 0, 0, 0));
            (await v1.SyncAsync()).Should().Be(new RecurringSyncResult(0, 0, 2, 0, 0), "a restart with the same code writes nothing");
            RecurringScheduleState a = await v1.ScheduleAsync("sync.a");
            a.Enabled.Should().BeTrue();
            a.JobType.Should().Be(Probe);
            a.MisfirePolicy.Should().Be(MisfirePolicy.FireOnce);
            a.AllowOverlap.Should().BeFalse();
            a.NextRunAtUtc.Should().BeAfter(await db.DbNowAsync());
            a.NextRunAtUtc.UtcDateTime.Hour.Should().Be(21, "02:00 Asia/Karachi is 21:00 UTC");
        }

        // v2 changes the cron of "a" and removes "b".
        await using (WorkerTestHost v2 = Host(db, b => b
            .AddRecurring("sync.a", "0 5 * * *", "UTC", new ProbePayload("a2"), new RecurringJobOptions { DefinitionVersion = 2, MisfirePolicy = MisfirePolicy.Skip })))
        {
            (await v2.SyncAsync()).Should().Be(new RecurringSyncResult(0, 1, 0, 0, 1));
            RecurringScheduleState a = await v2.ScheduleAsync("sync.a");
            (a.Cron, a.DefinitionVersion, a.MisfirePolicy).Should().Be(("0 5 * * *", 2, MisfirePolicy.Skip));
            a.NextRunAtUtc.UtcDateTime.Hour.Should().Be(5, "a changed cron recomputes the next run");
            (await v2.ScheduleAsync("sync.b")).Enabled.Should().BeFalse("a schedule removed from code is disabled, not deleted");
        }

        // An old instance restarting during the rolling deploy does not roll the definition back.
        await using (WorkerTestHost old = Host(db, b => b.AddRecurring("sync.a", "0 2 * * *", "Asia/Karachi", new ProbePayload("a"))))
        {
            RecurringSyncResult result = await old.SyncAsync();
            result.SkippedNewerStored.Should().Be(1);
            (await old.ScheduleAsync("sync.a")).Cron.Should().Be("0 5 * * *");
        }

        // An admin pause survives a sync; a re-added schedule is enabled again.
        await db.ExecuteAsync($"UPDATE ninjaqueue.job_recurring SET enabled = {db.Bool(false)}, paused_by = '{Guid.NewGuid()}' WHERE code = 'sync.a'");
        await using (WorkerTestHost v3 = Host(db, b => b
            .AddRecurring("sync.a", "0 5 * * *", "UTC", new ProbePayload("a2"), new RecurringJobOptions { DefinitionVersion = 2, MisfirePolicy = MisfirePolicy.Skip })
            .AddRecurring("sync.b", "0 3 * * *", "UTC", new ProbePayload("b"))))
        {
            await v3.SyncAsync();
            (await v3.ScheduleAsync("sync.a")).Enabled.Should().BeFalse("an admin pause is never overridden by code");
            RecurringScheduleState b = await v3.ScheduleAsync("sync.b");
            b.Enabled.Should().BeTrue();
            b.NextRunAtUtc.Should().BeAfter(await db.DbNowAsync(), "a re-added schedule restarts from now, without a misfire flood");
            (await v3.TickAsync()).Schedules.Should().Be(0, "paused and future schedules are not due");
        }
    }

    [Fact]
    public async Task Invalid_schedules_are_rejected_at_registration_or_host_start()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        FluentActions.Invoking(() => Host(db, b => b.AddRecurring("bad cron", "0 2 * * *", "UTC", new ProbePayload("x")))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Host(db, b => b.AddRecurring("x", "0 25 * * *", "UTC", new ProbePayload("x")))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Host(db, b => b.AddRecurring("x", "0 2 * * *", "Nowhere/City", new ProbePayload("x")))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Host(db, b => b.AddRecurring("x", "0 2 * * *", "UTC", new ProbePayload("x")).AddRecurring("x", "0 3 * * *", "UTC", new ProbePayload("y"))))
            .Should().Throw<ArgumentException>();

        await using WorkerTestHost unregistered = Host(db, b => b.AddRecurring("x", "0 2 * * *", "UTC", new RollingPayload("x")));
        FluentActions.Invoking(() => unregistered.Scheduler().Validate()).Should().Throw<JobRegistryException>().Which.Message.Should().Contain("no job type is registered");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Misfire_policies_after_five_days_of_downtime(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = Host(db, b => b
            .AddRecurring("mf.once", "0 0 * * *", "UTC", new ProbePayload("once"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireOnce, AllowOverlap = true })
            .AddRecurring("mf.all", "0 0 * * *", "UTC", new ProbePayload("all"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireAll, AllowOverlap = true })
            .AddRecurring("mf.skip", "0 0 * * *", "UTC", new ProbePayload("skip"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.Skip, AllowOverlap = true }));
        await host.SyncAsync();
        DateTimeOffset now = await db.DbNowAsync();
        DateTimeOffset today = new(now.UtcDateTime.Date, TimeSpan.Zero);
        if (now - today < TimeSpan.FromMinutes(2))
        {
            // Within the misfire threshold of midnight today's occurrence is on time, which this test does not model.
            await Task.Delay(TimeSpan.FromMinutes(2) - (now - today));
        }

        foreach (string code in (string[])["mf.once", "mf.all", "mf.skip"])
        {
            await db.SetNextRunAsync(code, today.AddDays(-5));
        }

        RecurringTickResult tick = await host.TickAsync();
        tick.Schedules.Should().Be(3);
        tick.Failed.Should().Be(0);

        (await db.OccurrenceKeysAsync("mf.once")).Should().Equal(Key("mf.once", today));
        (await db.OccurrenceKeysAsync("mf.all")).Should().Equal([.. Enumerable.Range(0, 6).Select(i => Key("mf.all", today.AddDays(i - 5)))]);
        (await db.OccurrenceKeysAsync("mf.skip")).Should().BeEmpty();
        foreach (string code in (string[])["mf.once", "mf.all", "mf.skip"])
        {
            RecurringScheduleState state = await host.ScheduleAsync(code);
            state.NextRunAtUtc.Should().Be(today.AddDays(1), $"{code} advances to the first occurrence after now");
            state.LastRunAtUtc.Should().NotBeNull();
        }

        (await host.TickAsync()).Schedules.Should().Be(0, "a second tick finds nothing due");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE job_type = '{Probe}'")).Should().Be(7);
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue")).Should().Be(7, "each fired occurrence is a claimable job");
        (await host.ScheduleAsync("mf.once")).LastJobId.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Fire_all_cap_keeps_the_latest_occurrences(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = Host(db, b => b.AddRecurring("cap", "0 * * * *", "UTC", new ProbePayload("cap"),
            new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireAll, AllowOverlap = true }), o => o.Recurring.FireAllCap = 3);
        await host.SyncAsync();
        DateTimeOffset now = await db.DbNowAsync();
        DateTimeOffset hour = new(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
        await db.SetNextRunAsync("cap", hour.AddHours(-10));

        await host.TickAsync();

        (await db.OccurrenceKeysAsync("cap")).Should().Equal(Key("cap", hour.AddHours(-2)), Key("cap", hour.AddHours(-1)), Key("cap", hour));
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Dst_transitions_produce_the_right_utc_occurrences_through_the_database(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = Host(db, b => b
            .AddRecurring("dst.spring", "30 2 * * *", "America/New_York", new ProbePayload("spring"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireAll, AllowOverlap = true })
            .AddRecurring("dst.fall", "30 1 * * *", "America/New_York", new ProbePayload("fall"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireAll, AllowOverlap = true }),
            o => o.Recurring.FireAllCap = 1000);
        await host.SyncAsync();

        // A catch-up from before each transition replays it: spring 2026-03-08, fall 2025-11-02 (America/New_York).
        await db.SetNextRunAsync("dst.spring", Utc(2026, 3, 6, 7, 30));
        await db.SetNextRunAsync("dst.fall", Utc(2025, 10, 31, 5, 30));
        (await host.TickAsync()).Failed.Should().Be(0);

        List<string> spring = await db.OccurrenceKeysAsync("dst.spring");
        spring.Should().Contain(Key("dst.spring", Utc(2026, 3, 7, 7, 30)), "02:30 EST");
        spring.Should().Contain(Key("dst.spring", Utc(2026, 3, 8, 7, 0)), "the skipped 02:30 fires at the end of the gap, 03:00 EDT");
        spring.Should().NotContain(Key("dst.spring", Utc(2026, 3, 8, 7, 30)));
        spring.Should().Contain(Key("dst.spring", Utc(2026, 3, 9, 6, 30)), "02:30 EDT");

        List<string> fall = await db.OccurrenceKeysAsync("dst.fall");
        fall.Should().Contain(Key("dst.fall", Utc(2025, 11, 2, 5, 30)), "01:30 EDT, the first instance");
        fall.Should().NotContain(Key("dst.fall", Utc(2025, 11, 2, 6, 30)), "the repeated 01:30 EST is not fired");
        fall.Should().Contain(Key("dst.fall", Utc(2025, 11, 3, 6, 30)), "01:30 EST the next day");
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE idempotency_key LIKE 'recurring:dst.fall:2025-11-02%'")).Should().Be(1, "one occurrence on the fall-back day");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Two_instances_ticking_concurrently_enqueue_exactly_one_job_per_occurrence(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        static void Schedules(NinjaQueueBuilder b)
        {
            for (int i = 0; i < 5; i++)
            {
                b.AddRecurring($"race.{i}", "* * * * * *", "UTC", new ProbePayload($"race-{i}"), new RecurringJobOptions { MisfirePolicy = MisfirePolicy.FireAll, AllowOverlap = true });
            }
        }

        await using WorkerTestHost a = Host(db, Schedules);
        await using WorkerTestHost b = Host(db, Schedules);
        await Task.WhenAll(a.SyncAsync(), b.SyncAsync());
        DateTimeOffset start = await db.DbNowAsync();
        for (int i = 0; i < 5; i++)
        {
            await db.SetNextRunAsync($"race.{i}", new DateTimeOffset(start.UtcDateTime.AddTicks(-(start.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero).AddSeconds(-20));
        }

        int failed = 0;
        ConcurrentQueue<string> errors = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(6));
        async Task Loop(WorkerTestHost host)
        {
            while (!stop.IsCancellationRequested)
            {
                RecurringTickResult r = await host.TickAsync();
                Interlocked.Add(ref failed, r.Failed);
                foreach ((string code, Exception error) in r.Failures)
                {
                    errors.Enqueue($"{code}: {error.GetType().Name}: {error.Message} | inner: {error.InnerException?.GetType().Name} {error.InnerException?.Message}");
                }
            }
        }

        await Task.WhenAll(Loop(a), Loop(a), Loop(b), Loop(b));

        failed.Should().Be(0, string.Join(Environment.NewLine, errors));
        (await db.ScalarAsync<int>("SELECT count(*) FROM (SELECT idempotency_key FROM ninjaqueue.background_jobs GROUP BY idempotency_key HAVING count(*) > 1) d"))
            .Should().Be(0, "never two jobs for one occurrence");
        (await db.EventCountAsync(JobEventCodes.JobDeduplicated)).Should().Be(0, "the locked schedule row is the fence: no scheduler even attempted a duplicate");
        for (int i = 0; i < 5; i++)
        {
            List<string> keys = await db.OccurrenceKeysAsync($"race.{i}");
            keys.Count.Should().BeGreaterThan(20, "the 20 s backlog plus the occurrences during the run");
            List<DateTimeOffset> occurrences = [.. keys.Select(k => DateTimeOffset.Parse(k[$"recurring:race.{i}:".Length..], System.Globalization.CultureInfo.InvariantCulture)).Order()];
            occurrences.Zip(occurrences.Skip(1), (x, y) => y - x).Should().AllSatisfy(gap => gap.Should().Be(TimeSpan.FromSeconds(1)), "no occurrence is lost or doubled");
        }
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Overlap_is_skipped_once_on_the_blocking_job_and_never_repeats(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = Host(db, b => b.AddRecurring("overlap", "0 0 * * *", "UTC", new ProbePayload("overlap")));
        await host.SyncAsync();

        await db.MakeDueAsync("overlap");
        RecurringTickResult first = await host.TickAsync();
        first.Enqueued.Should().Be(1);
        string blocker = (await db.OccurrenceKeysAsync("overlap")).Single();

        // The next occurrence is due while the previous job is still active (Enqueued, not yet run).
        await db.MakeDueAsync("overlap");
        RecurringTickResult second = await host.TickAsync();
        second.Should().BeEquivalentTo(new { Schedules = 1, Enqueued = 0, SkippedOverlap = 1, Failed = 0 });
        RecurringScheduleState state = await host.ScheduleAsync("overlap");
        state.NextRunAtUtc.Should().BeAfter(await db.DbNowAsync(), "the schedule advances even when it skips");

        for (int i = 0; i < 10; i++)
        {
            (await host.TickAsync()).Schedules.Should().Be(0);
        }

        (await db.EventCountAsync(JobEventCodes.RecurringSkippedOverlap)).Should().Be(1, "exactly one skip, recorded once");
        (await db.ScalarAsync<string>($"""
            SELECT b.idempotency_key FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id
             WHERE e.event_code = '{JobEventCodes.RecurringSkippedOverlap}'
            """)).Should().Be(blocker, "the event is on the blocking job");
        (await db.ScalarAsync<string>($"SELECT data FROM ninjaqueue.job_events WHERE event_code = '{JobEventCodes.RecurringSkippedOverlap}'")).Should().Contain("\"schedule\":\"overlap\"");
        (await db.OccurrenceKeysAsync("overlap")).Should().HaveCount(1);

        // Once the blocker finishes, the key is free and the next occurrence enqueues.
        (await host.RunOneAsync(Probe)).Should().NotBeNull();
        await db.MakeDueAsync("overlap");
        (await host.TickAsync()).Enqueued.Should().Be(1);
        (await db.OccurrenceKeysAsync("overlap")).Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Hosted_scheduler_syncs_and_fires_while_holding_the_lease(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = Host(db, b => b.AddRecurring("hosted", "* * * * * *", "UTC", new ProbePayload("hosted")), o =>
        {
            o.Recurring.Enabled = true;
            o.Recurring.TickInterval = TimeSpan.FromMilliseconds(500);
            o.Recurring.MisfireThreshold = TimeSpan.FromSeconds(5);
        });
        await host.StartAsync();

        (await JobProbes.EventuallyAsync(async () => await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs b WHERE b.idempotency_key LIKE 'recurring:hosted:%' AND b.status = {(short)JobStatus.Succeeded}") >= 2,
            TimeSpan.FromSeconds(20))).Should().BeTrue("the scheduler fires every second and the worker runs the jobs");
        (await db.ScalarAsync<string>("SELECT owner FROM ninjaqueue.job_leases WHERE name = 'scheduler.recurring'")).Should().EndWith(":recurring");
        await host.StopAsync();
        (await db.ScalarAsync<int>($"SELECT CASE WHEN expires_at_utc <= {db.Now} THEN 1 ELSE 0 END FROM ninjaqueue.job_leases WHERE name = 'scheduler.recurring'")).Should().Be(1, "stopping releases the lease");
    }
}