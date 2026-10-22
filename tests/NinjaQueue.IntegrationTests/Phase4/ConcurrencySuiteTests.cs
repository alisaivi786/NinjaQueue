using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// §14.8 concurrency suite (Release 1 features), with the §14.8 / §16.5 G6 invariant checker after every scenario: several
/// workers (separate DI containers, each a full <c>NinjaQueueWorkerHost</c>) race to claim and run the same jobs; concurrent
/// enqueues share one idempotency key or one unique key. Job count: <c>NINJA_JOBS_SUITE_JOBS</c> (default 1 500).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
[Trait("Gate", "Concurrency")]
public sealed class ConcurrencySuiteTests(JobsDatabaseFixture fixture, ITestOutputHelper output)
{
    private const int Workers = 4;
    private const int ConcurrencyPerWorker = 6;

    private static int JobCount => int.TryParse(Environment.GetEnvironmentVariable("NINJA_JOBS_SUITE_JOBS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1500;

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Concurrent_workers_run_every_job_exactly_once_with_no_losses(DatabaseKind kind)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        List<Phase4Host> hosts = [.. Enumerable.Range(0, Workers).Select(_ => Phase4Host.Build(db, o => o.Worker.Queues = new(StringComparer.Ordinal) { ["default"] = ConcurrencyPerWorker }))];
        try
        {
            int total = JobCount;
            int fencedCount = total * 2 / 3;
            IReadOnlyList<JobId> fenced = await hosts[0].EnqueueManyAsync(Enumerable.Range(0, fencedCount).Select(i => new CountPayload(i)).ToList());
            // About 3 % of standard jobs fail once and are retried; every job must still succeed exactly once.
            IReadOnlyList<JobId> standard = await hosts[0].EnqueueManyAsync(Enumerable.Range(0, total - fencedCount)
                .Select(i => new P4Payload("succeed", FailFirstAttempts: i % 29 == 3 ? 1 : 0)).ToList());

            Stopwatch watch = Stopwatch.StartNew();
            await Task.WhenAll(hosts.Select(h => h.StartWorkersAsync()));
            await WaitAsync(async () => await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status < 10") == 0, TimeSpan.FromMinutes(8));
            TimeSpan took = watch.Elapsed;
            await Task.WhenAll(hosts.Select(h => h.StopWorkersAsync()));

            // Zero lost jobs: every job succeeded.
            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = 10")).Should().Be(total);

            // Zero duplicate executions. Fenced: exactly one business row per job (§14.8 invariant 4), including the jobs whose
            // first attempt failed after writing. Standard: exactly one successful handler return per job across all workers.
            Dictionary<string, int> rows = (await db.QueryAsync("SELECT name, count(*) FROM test_widgets GROUP BY name"))
                .ToDictionary(r => (string)r[0]!, r => Phase4Host.Int(r[1]), StringComparer.Ordinal);
            fenced.Where(id => rows.GetValueOrDefault($"{id.Value:N}-count") != 1).Should().BeEmpty("each fenced job committed its business row exactly once");
            rows.Values.Sum().Should().Be(fencedCount);
            Dictionary<Guid, int> successes = hosts.SelectMany(h => h.Probe.Successes).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            standard.Where(id => successes.GetValueOrDefault(id.Value) != 1).Should().BeEmpty("each standard job succeeded exactly once");
            int retried = standard.Count(id => hosts.Sum(h => h.Probe.Invocations.GetValueOrDefault(id.Value)) > 1);
            retried.Should().Be(Enumerable.Range(0, total - fencedCount).Count(i => i % 29 == 3), "only the injected failures ran twice");

            int workersUsed = await db.ScalarAsync<int>("SELECT count(DISTINCT worker_id) FROM ninjaqueue.job_attempts");
            workersUsed.Should().Be(Workers, "every worker took part in the race");
            output.WriteLine($"{kind}: {total} jobs ({fencedCount} fenced, {total - fencedCount} standard, {retried} retried) by {Workers} workers x {ConcurrencyPerWorker} in {took.TotalSeconds:F1} s ({total / took.TotalSeconds:F0} jobs/s)");
            await JobInvariants.AssertHoldAsync(db);
        }
        finally
        {
            foreach (Phase4Host host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Concurrent_enqueues_with_one_idempotency_or_unique_key_create_one_job(DatabaseKind kind)
    {
        TestDatabase db = await Phase4Host.CreateDatabaseAsync(fixture, kind);
        List<Phase4Host> hosts = [.. Enumerable.Range(0, Workers).Select(_ => Phase4Host.Build(db))];
        try
        {
            const int Racers = 24;
            for (int round = 0; round < 5; round++)
            {
                // One idempotency key from many instances at once: one job, the rest deduplicated onto it.
                JobOptions idempotent = new() { IdempotencyKey = $"idem:{round}" };
                JobId[] ids = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i => hosts[i % Workers].EnqueueAsync(new P4Payload("succeed"), idempotent)));
                ids.Distinct().Should().ContainSingle();
                (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE idempotency_key = 'idem:{round}'")).Should().Be(1);
                (await hosts[0].EventsAsync(ids[0])).Count(e => e == JobEventCodes.JobDeduplicated).Should().Be(Racers - 1);

                // One unique key (ReturnExisting): one active job.
                JobOptions unique = new() { UniqueKey = $"uniq:{round}", Queue = $"u{round}" };
                JobId[] holders = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i => hosts[i % Workers].EnqueueAsync(new P4Payload("succeed"), unique)));
                holders.Distinct().Should().ContainSingle();

                // One unique key (Reject): exactly one enqueue wins once the holder is gone.
                (await hosts[0].RunUntilTerminalAsync(holders[0], "Test.P4.Standard.v1", $"u{round}")).Should().Be(JobStatus.Succeeded);
                Task<JobId>[] rejecting = [.. Enumerable.Range(0, Racers).Select(i => hosts[i % Workers].EnqueueAsync(new P4Payload("succeed"), unique with { OnUniqueKeyConflict = UniqueKeyConflict.Reject }))];
                await Task.WhenAll(rejecting.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
                rejecting.Count(t => t.IsCompletedSuccessfully).Should().Be(1, "one enqueue takes the freed key");
                rejecting.Where(t => t.IsFaulted).Should().OnlyContain(t => t.Exception!.InnerException is JobConflictException);
                (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE unique_key = 'uniq:{round}' AND status < 10")).Should().Be(1);
                await JobInvariants.AssertHoldAsync(db);
            }

            (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs")).Should().Be(5 * 3, "one idempotent job and two unique-key generations per round");
        }
        finally
        {
            foreach (Phase4Host host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    internal static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }

            await Task.Delay(100);
        }
    }
}