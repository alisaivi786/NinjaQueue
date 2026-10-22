using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NinjaQueue.Hosting;
using NinjaQueue.Observability;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Phase 5 / 6a maintenance (§14.6, §14.7, §15.9): the two-tier retention purge (children first, in batches), named
/// maintenance leases (only the holder runs a task; a released lease is taken over) and two leaders at once being harmless.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "5")]
public sealed class MaintenanceTests(JobsDatabaseFixture fixture)
{
    private const string Probe = "Test.Probe.v1";

    private Task<JobsHost> HostAsync(DatabaseKind kind, Action<NinjaQueueOptions>? configure = null)
        => JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            b.Services.AddSingleton<ProbePlan>();
        }, configure);

    private static Task<int> PurgeAsync(JobsHost host)
        => host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().PurgeAsync(host.Get<IOptions<NinjaQueueOptions>>().Value.Retention, CancellationToken.None));

    private static async Task<List<JobId>> RunJobsAsync(JobsHost host, int count, string prefix)
    {
        List<JobId> ids = [];
        for (int i = 0; i < count; i++)
        {
            ids.Add(await host.EnqueueAsync(new ProbePayload($"{prefix}-{i}")));
        }

        while (await host.RunOneAsync(Probe) is not null)
        {
        }

        return ids;
    }

    private static string Ago(TestDatabase db, int days)
        => db.IsPostgres ? $"now() - interval '{days} days'" : $"DATEADD(day, -{days}, SYSDATETIMEOFFSET())";

    private static string In(IEnumerable<JobId> ids) => string.Join(",", ids.Select(i => $"'{i.Value}'"));

    private static Task<int> ChildRowsAsync(TestDatabase db, IEnumerable<JobId> ids)
        => db.ScalarAsync<int>($"""
            SELECT (SELECT count(*) FROM ninjaqueue.job_attempts a JOIN ninjaqueue.background_jobs b ON b.id = a.job_id WHERE b.guid_id IN ({In(ids)}))
                 + (SELECT count(*) FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id WHERE b.guid_id IN ({In(ids)}))
            """);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Operational_purge_slims_terminal_jobs_in_batches_children_first(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind, o => o.Retention.PurgeBatchSize = 3);
        List<JobId> old = await RunJobsAsync(host, 7, "old");
        List<JobId> retained = await RunJobsAsync(host, 1, "retained");
        List<JobId> recent = await RunJobsAsync(host, 1, "recent");
        JobId pending = await host.EnqueueAsync(new ProbePayload("pending"), new JobOptions { RunAt = DateTimeOffset.UtcNow.AddDays(1) });
        TestDatabase db = host.Database;
        await db.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET completed_at_utc = {Ago(db, 20)} WHERE status = 10");
        await db.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET completed_at_utc = {db.Now} WHERE guid_id IN ({In(recent)})");
        await db.ExecuteAsync(db.IsPostgres
            ? $"UPDATE ninjaqueue.background_jobs SET retain_until_utc = now() + interval '1 day' WHERE guid_id IN ({In(retained)})"
            : $"UPDATE ninjaqueue.background_jobs SET retain_until_utc = DATEADD(day, 1, SYSDATETIMEOFFSET()) WHERE guid_id IN ({In(retained)})");
        Task<int> Slimmed() => db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE payload IS NULL AND guid_id IN ({In(old)})");

        int[] perRound = new int[4];
        int[] slimmedAfter = new int[4];
        for (int round = 0; round < 4; round++)
        {
            perRound[round] = await PurgeAsync(host);
            slimmedAfter[round] = await Slimmed();
        }

        slimmedAfter.Should().Equal([3, 6, 7, 7], "one batch of PurgeBatchSize (3) per round");
        perRound[0].Should().Be((3 * (1 + 3)) + 3, "per job 1 attempt and 3 events are deleted, then the 3 rows are slimmed");
        perRound[3].Should().Be(0);
        (await ChildRowsAsync(db, old)).Should().Be(0, "attempts and events of slimmed jobs are gone");
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(10, "the operational tier keeps the slim audit row");
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE payload IS NOT NULL AND guid_id IN ({In(retained.Concat(recent).Append(pending))})"))
            .Should().Be(3, "retained, recent and pending jobs are untouched");
        (await ChildRowsAsync(db, retained.Concat(recent))).Should().Be(2 * 4);
        (await db.StatusOfAsync(old[0])).Should().Be(JobStatus.Succeeded);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Audit_purge_deletes_rows_past_retention_after_their_children_in_batches(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind, o => o.Retention.PurgeBatchSize = 2);
        host.Get<ProbePlan>().Behaviour = (p, _, _) => p.Name.StartsWith("dead", StringComparison.Ordinal)
            ? throw new PermanentJobException("x")
            : Task.FromResult(JobResult.Success());
        List<JobId> ancient = await RunJobsAsync(host, 3, "ancient");
        List<JobId> dead = await RunJobsAsync(host, 2, "dead");
        List<JobId> young = await RunJobsAsync(host, 1, "young");
        TestDatabase db = host.Database;
        List<JobId> expired = [.. ancient, .. dead];
        await db.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET completed_at_utc = {Ago(db, 401)} WHERE guid_id IN ({In(expired)})");
        (await ChildRowsAsync(db, expired)).Should().BeGreaterThan(0, "rows still carry children: only the FK-safe order can delete them");

        Task<int> Remaining() => db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE guid_id IN ({In(expired)})");
        List<int> remaining = [];
        for (int round = 0; round < 4; round++)
        {
            await PurgeAsync(host);
            remaining.Add(await Remaining());
        }

        // Per round the operational tier slims (status by status) and the audit tier deletes up to 2 rows.
        remaining.Should().Equal([3, 1, 0, 0], "the audit tier deletes PurgeBatchSize (2) rows per round");
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_attempts a WHERE NOT EXISTS (SELECT 1 FROM ninjaqueue.background_jobs b WHERE b.id = a.job_id)")).Should().Be(0);
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(1);
        (await db.StatusOfAsync(young[0])).Should().Be(JobStatus.Succeeded);
        (await ChildRowsAsync(db, young)).Should().Be(4);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Only_the_maintenance_lease_holder_runs_a_task_and_a_released_lease_is_taken_over(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind, o => o.Worker.MaintenanceInterval = TimeSpan.FromSeconds(20));
        NinjaQueueOptions options = host.Get<IOptions<NinjaQueueOptions>>().Value;
        Guid tokenA = Guid.NewGuid();
        Guid tokenB = Guid.NewGuid();
        MaintenanceLoop loopA = new(host.Get<IServiceScopeFactory>(), options, host.Get<JobMetrics>(), "instance-a", tokenA, NullLogger.Instance);
        MaintenanceLoop loopB = new(host.Get<IServiceScopeFactory>(), options, host.Get<JobMetrics>(), "instance-b", tokenB, NullLogger.Instance);

        async Task<JobId> ExpiredLeaseAsync(string name)
        {
            await host.EnqueueAsync(new ProbePayload(name));
            ClaimedJob claimed = (await host.ClaimWithLeaseAsync(Probe, TimeSpan.FromMinutes(1)))!;
            await host.Database.ExpireLeaseAsync(claimed.Id);
            return claimed.JobId;
        }

        Task<List<object?[]>> Owners() => host.Database.QueryAsync(
            $"SELECT name, owner, CASE WHEN expires_at_utc > {host.Database.Now} THEN 1 ELSE 0 END FROM ninjaqueue.job_leases WHERE name IN ({string.Join(",", MaintenanceLoop.LeaseNames.Select(n => $"'{n}'"))})");

        JobId first = await ExpiredLeaseAsync("first");
        await loopA.RunOnceAsync(CancellationToken.None);
        (await host.StatusAsync(first)).Should().Be(JobStatus.Scheduled, "A took the leases and ran recovery");
        (await Owners()).Should().HaveCount(MaintenanceLoop.LeaseNames.Length).And.OnlyContain(r => (string)r[1]! == "instance-a" && Convert.ToInt32(r[2], System.Globalization.CultureInfo.InvariantCulture) == 1);

        JobId second = await ExpiredLeaseAsync("second");
        await loopB.RunOnceAsync(CancellationToken.None);
        (await host.StatusAsync(second)).Should().Be(JobStatus.Processing, "B holds no lease, so B runs no task");
        (await Owners()).Should().OnlyContain(r => (string)r[1]! == "instance-a");

        await loopA.RunOnceAsync(CancellationToken.None);
        (await host.StatusAsync(second)).Should().Be(JobStatus.Scheduled, "the holder renews its lease and keeps running the tasks");

        // A releases (shutdown cleanup): B takes over at once, without waiting for the TTL.
        await host.InScopeAsync(async sp =>
        {
            foreach (string name in MaintenanceLoop.LeaseNames)
            {
                await sp.GetRequiredService<IJobMaintenanceStore>().ReleaseLeaseAsync(name, tokenA, CancellationToken.None);
            }
        });
        JobId third = await ExpiredLeaseAsync("third");
        await loopB.RunOnceAsync(CancellationToken.None);
        (await host.StatusAsync(third)).Should().Be(JobStatus.Scheduled);
        (await Owners()).Should().OnlyContain(r => (string)r[1]! == "instance-b");

        // A stale owner's release does nothing to the new holder's lease.
        await host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().ReleaseLeaseAsync(MaintenanceLoop.Recovery, tokenA, CancellationToken.None));
        (await Owners()).Should().OnlyContain(r => (string)r[1]! == "instance-b" && Convert.ToInt32(r[2], System.Globalization.CultureInfo.InvariantCulture) == 1);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Two_leaders_recovering_at_once_recover_each_job_exactly_once(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        List<JobId> ids = [];
        for (int i = 0; i < 40; i++)
        {
            ids.Add(await host.EnqueueAsync(new ProbePayload($"j{i}")));
        }

        IReadOnlyList<ClaimedJob> claimed = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>()
            .ClaimAsync("default", [Probe], "dead-worker", 40, TimeSpan.FromMinutes(1), CancellationToken.None));
        claimed.Should().HaveCount(40);
        await host.Database.ExecuteAsync(host.Database.IsPostgres
            ? "UPDATE ninjaqueue.job_queue SET lease_expires_at_utc = now() - interval '1 second'"
            : "UPDATE ninjaqueue.job_queue SET lease_expires_at_utc = DATEADD(second, -1, SYSDATETIMEOFFSET())");

        int[] recovered = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobMaintenanceStore>().RecoverExpiredLeasesAsync(15, _ => TimeSpan.Zero, CancellationToken.None)))));
        int more = 0;
        while (await host.RecoverAsync() is var n and > 0)
        {
            more += n;
        }

        (recovered.Sum() + more).Should().Be(40, "every expired lease is recovered exactly once, whoever runs");
        (await host.Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_events WHERE event_code = '{JobEventCodes.JobLeaseExpired}'")).Should().Be(40);
        (await host.Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = {(short)JobStatus.Scheduled} AND attempt = 1")).Should().Be(40);
        (await host.Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_attempts WHERE outcome = {(short)JobAttemptOutcome.LeaseLost} AND charged = {host.Database.Bool(true)}")).Should().Be(40);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue WHERE lease_token IS NOT NULL OR attempts_left <> 2")).Should().Be(0);
    }
}