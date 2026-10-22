
namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// Concurrent enqueues of one job type with different idempotency (or unique) keys never deadlock, while one key still
/// dedupes to exactly one job (§15.3, §14.8). SQL Server once took serializable key-range locks for the "not found" probe:
/// two transactions holding RangeS-U on the same gap of the unique index then each waited for the other's RangeI-N insert
/// (error 1205). PostgreSQL 16, SQL Server 2022 RCSI ON and OFF.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
public sealed class EnqueueDeadlockTests(JobsDatabaseFixture fixture)
{
    // 24 racers on one key (the §14.8 dedupe shape); 16 per round on distinct keys keeps the shared test servers' connection
    // count reasonable while still overlapping every transaction with every other.
    private const int Racers = 24;
    private const int DistinctRacers = 16;
    private const int Rounds = 5;

    private static Task<JobId> StageInTransactionAsync(Phase4Host host, JobOptions options, int holdMs)
        => host.InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>().UseTransactionAsync(async (session, ct) =>
        {
            JobId id = await sp.GetRequiredService<IJobEnqueuer>().StageAsync(new P4Payload("succeed"), options, ct);

            // Hold the transaction (and whatever the enqueue locked) open while the other racers enqueue.
            await Task.Delay(holdMs, ct);
            return id;
        }));

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Parallel_transactions_with_different_idempotency_keys_never_deadlock(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        for (int round = 0; round < Rounds; round++)
        {
            int r = round;
            JobId[] ids = await Task.WhenAll(Enumerable.Range(0, DistinctRacers).Select(i => Task.Run(() =>
                StageInTransactionAsync(host, new JobOptions { IdempotencyKey = $"order:{r}:{i:D3}" }, holdMs: 150))));

            ids.Distinct().Should().HaveCount(DistinctRacers, "every key is its own job");
        }

        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(DistinctRacers * Rounds);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Parallel_transactions_with_different_unique_keys_never_deadlock(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        for (int round = 0; round < Rounds; round++)
        {
            int r = round;
            JobId[] ids = await Task.WhenAll(Enumerable.Range(0, DistinctRacers).Select(i => Task.Run(() =>
                StageInTransactionAsync(host, new JobOptions { UniqueKey = $"tenant-sync:{r}:{i:D3}" }, holdMs: 150))));

            ids.Distinct().Should().HaveCount(DistinctRacers);
        }

        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_active_keys")).Should().Be(DistinctRacers * Rounds);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Parallel_transactions_with_one_idempotency_key_still_create_exactly_one_job(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);

        JobId[] ids = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => Task.Run(() =>
            StageInTransactionAsync(host, new JobOptions { IdempotencyKey = "order:same" }, holdMs: 50))));

        ids.Distinct().Should().ContainSingle("one key dedupes to one job");
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(1);
        (await host.Database.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_events WHERE event_code = '{JobEventCodes.JobDeduplicated}'")).Should().Be(Racers - 1);
    }
}