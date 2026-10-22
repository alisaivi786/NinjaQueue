using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

/// <summary>
/// Concurrency keys and MaxRunningPerTenant (§14.5, Release 2): one running job per (tenant, key), keys scoped by tenant,
/// a per-tenant cap enforced by slots, serialization across several workers on PostgreSQL and SQL Server RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "7b")]
public sealed class ConcurrencyKeyTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task The_claim_takes_one_job_per_tenant_and_key_and_the_next_after_it_ends(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        JobOptions a = new() { TenantId = P7b.TenantA, ConcurrencyKey = "payroll-run:1" };
        JobOptions b = new() { TenantId = P7b.TenantB, ConcurrencyKey = "payroll-run:1" };
        List<JobId> aJobs = [];
        for (int i = 0; i < 4; i++)
        {
            aJobs.Add(await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, $"a{i}"), a));
        }

        JobId bJob = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantB, "b0"), b);
        JobId unkeyed = await host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "u"), new JobOptions { TenantId = P7b.TenantA });
        (await host.Database.JobColumnAsync<string>(aJobs[0], "concurrency_key")).Should().Be("payroll-run:1");

        IReadOnlyList<ClaimedJob> first = await host.ClaimAsync([P7b.Keyed], maxJobs: 20);
        first.Select(j => j.JobId).Should().BeEquivalentTo([unkeyed, aJobs[0], bJob], "the same key string never serializes another tenant (§14.5)");
        (await host.ClaimAsync([P7b.Keyed], maxJobs: 20)).Should().BeEmpty("payroll-run:1 is running for both tenants");
        await P7b.AssertInvariantsAsync(host.Database);

        foreach (ClaimedJob job in first)
        {
            (await host.ExecuteAsync(job)).Should().Be(Execution.ExecutionResult.Succeeded);
        }

        // Each finished job frees its key: exactly the next job of tenant A's key is claimable, FIFO.
        for (int i = 1; i < 4; i++)
        {
            IReadOnlyList<ClaimedJob> next = await host.ClaimAsync([P7b.Keyed], maxJobs: 20);
            next.Select(j => j.JobId).Should().Equal(aJobs[i]);
            (await host.ExecuteAsync(next[0])).Should().Be(Execution.ExecutionResult.Succeeded);
        }

        foreach (JobId id in aJobs.Append(bJob))
        {
            (await host.StatusAsync(id)).Should().Be(JobStatus.Succeeded);
        }

        await P7b.AssertInvariantsAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Workers_on_two_instances_serialize_a_key_and_cap_a_tenant(DatabaseKind kind)
    {
        Action<NinjaQueueOptions> workers = o =>
        {
            o.Worker.Queues = new Dictionary<string, int>(StringComparer.Ordinal) { ["default"] = 6 };

            // A short lease (still above the Phase 4 fenced handlers' 20 s step timeout), so an outcome lost to a database
            // hiccup is recovered within the test, as maintenance would.
            o.Worker.LeaseDuration = TimeSpan.FromSeconds(25);
            o.Worker.LeaseSafetyMargin = TimeSpan.FromSeconds(3);
        };
        await using Phase4Host one = await P7b.HostAsync(fixture, kind, workers);
        await using Phase4Host two = P7b.On(one.Database, workers);

        List<JobId> keyed = [];
        for (int i = 0; i < 12; i++)
        {
            keyed.Add(await one.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, $"k{i}", 60, "key"), new JobOptions { TenantId = P7b.TenantA, ConcurrencyKey = "team-channel:9" }));
        }

        List<JobId> fair = [];
        for (int i = 0; i < 10; i++)
        {
            fair.Add(await one.EnqueueAsync(new P7bFairPayload(P7b.TenantA, 120), new JobOptions { TenantId = P7b.TenantA }));
        }

        for (int i = 0; i < 4; i++)
        {
            fair.Add(await one.EnqueueAsync(new P7bFairPayload(P7b.TenantB, 120), new JobOptions { TenantId = P7b.TenantB }));
        }

        (await one.Database.JobColumnAsync<string>(fair[0], "concurrency_key")).Should().Be("ninja:slots:2:Test.P7b.Fair.v1");
        await one.StartWorkersAsync();
        await two.StartWorkersAsync();

        bool done = await JobProbes.EventuallyAsync(
            async () =>
            {
                await one.RecoverAsync();
                return await one.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = 10") == keyed.Count + fair.Count;
            }, TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(500));
        if (!done)
        {
            List<object?[]> byStatus = await one.Database.QueryAsync("SELECT status, count(*) FROM ninjaqueue.background_jobs GROUP BY status");
            List<object?[]> queue = await one.Database.QueryAsync("SELECT concurrency_key, CASE WHEN lease_token IS NULL THEN 0 ELSE 1 END, count(*) FROM ninjaqueue.job_queue GROUP BY concurrency_key, CASE WHEN lease_token IS NULL THEN 0 ELSE 1 END");
            string logs = string.Join(" | ", one.Get<CapturingLoggerProvider>().Lines.Concat(two.Get<CapturingLoggerProvider>().Lines).Take(6));
            done.Should().BeTrue($"every keyed and capped job completes; status {string.Join(",", byStatus.Select(r => $"{r[0]}={r[1]}"))}; "
                + $"queue {string.Join(",", queue.Select(r => $"{r[0]}/{r[1]}={r[2]}"))}; log {logs}");
        }

        await one.StopWorkersAsync();
        await two.StopWorkersAsync();

        int MaxSeen(string key) => Math.Max(one.Plan().MaxRunning.GetValueOrDefault(key), two.Plan().MaxRunning.GetValueOrDefault(key));
        one.Plan().MaxRunning.GetValueOrDefault("key").Should().BeLessThanOrEqualTo(1);
        two.Plan().MaxRunning.GetValueOrDefault("key").Should().BeLessThanOrEqualTo(1);
        (await OverlapsAsync(one.Database, keyed)).Should().Be(0, "no two jobs of one key ran at the same time, across instances");
        MaxSeen($"fair:{P7b.TenantA}").Should().BeLessThanOrEqualTo(2);
        (await MaxConcurrentAsync(one.Database, fair.Take(10).ToList())).Should().BeLessThanOrEqualTo(2, "tenant A never ran more than MaxRunningPerTenant = 2");
        (await MaxConcurrentAsync(one.Database, fair)).Should().BeGreaterThan(2, "tenant B ran while tenant A was at its cap");
        await P7b.AssertInvariantsAsync(one.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Concurrent_claims_never_lease_one_key_twice(DatabaseKind kind)
    {
        await using Phase4Host one = await P7b.HostAsync(fixture, kind);
        await using Phase4Host two = P7b.On(one.Database);
        for (int i = 0; i < 30; i++)
        {
            await one.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, $"r{i}"), new JobOptions { TenantId = P7b.TenantA, ConcurrencyKey = $"k:{i % 3}" });
        }

        for (int round = 0; round < 10; round++)
        {
            IReadOnlyList<ClaimedJob>[] claims = await Task.WhenAll(
                one.ClaimAsync([P7b.Keyed], maxJobs: 5, worker: "w1"), two.ClaimAsync([P7b.Keyed], maxJobs: 5, worker: "w2"),
                one.ClaimAsync([P7b.Keyed], maxJobs: 5, worker: "w3"));
            ClaimedJob[] all = [.. claims.SelectMany(c => c)];
            all.Length.Should().BeLessThanOrEqualTo(3, "three keys, one running job each");
            await P7b.AssertInvariantsAsync(one.Database);
            foreach (ClaimedJob job in all)
            {
                (await one.ExecuteAsync(job)).Should().Be(Execution.ExecutionResult.Succeeded);
            }
        }

        (await one.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = 10")).Should().Be(30);
        await P7b.AssertInvariantsAsync(one.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task A_capped_type_rejects_an_explicit_key(DatabaseKind kind)
    {
        await using Phase4Host host = await P7b.HostAsync(fixture, kind);
        await FluentActions.Awaiting(() => host.EnqueueAsync(new P7bFairPayload(P7b.TenantA, 0), new JobOptions { TenantId = P7b.TenantA, ConcurrencyKey = "x:1" }))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => host.EnqueueAsync(new P7bTenantPayload(P7b.TenantA, "x"), new JobOptions { TenantId = P7b.TenantA, ConcurrencyKey = "has space" }))
            .Should().ThrowAsync<ArgumentException>();
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(0);
    }

    // Pairs of jobs whose successful attempts overlapped in time (database clock).
    private static Task<int> OverlapsAsync(TestDatabase db, List<JobId> ids)
    {
        string list = string.Join(",", ids.Select(i => $"'{i.Value}'"));
        return db.ScalarAsync<int>($"""
            SELECT count(*) FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs ba ON ba.id = a.job_id
              JOIN ninjaqueue.job_attempts b ON b.id <> a.id JOIN ninjaqueue.background_jobs bb ON bb.id = b.job_id
             WHERE ba.guid_id IN ({list}) AND bb.guid_id IN ({list}) AND a.outcome = 1 AND b.outcome = 1
               AND a.started_at_utc < b.finished_at_utc AND b.started_at_utc < a.finished_at_utc
            """);
    }

    // The most successful attempts of these jobs running at one instant (checked at each attempt's start).
    private static Task<int> MaxConcurrentAsync(TestDatabase db, List<JobId> ids)
    {
        string list = string.Join(",", ids.Select(i => $"'{i.Value}'"));
        return db.ScalarAsync<int>($"""
            SELECT COALESCE(MAX(n), 0) FROM (
              SELECT (SELECT count(*) FROM ninjaqueue.job_attempts b JOIN ninjaqueue.background_jobs bb ON bb.id = b.job_id
                       WHERE bb.guid_id IN ({list}) AND b.outcome = 1 AND b.started_at_utc <= a.started_at_utc AND b.finished_at_utc > a.started_at_utc) AS n
                FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs ba ON ba.id = a.job_id WHERE ba.guid_id IN ({list}) AND a.outcome = 1) x
            """);
    }
}