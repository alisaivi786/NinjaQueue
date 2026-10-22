using System.Collections.Concurrent;
using NinjaQueue.Storage;

namespace NinjaQueue.PerformanceTests;

/// <summary>
/// §14.8 "no deadlocks and no lock escalation at batch size ≤ 500 (track sys.dm_tran_locks)": eight claimers take batches of
/// 500 and complete them while two enqueuers keep adding jobs. Counts deadlocks (client errors and the server counter), lock
/// escalations (SQL Server), table-level locks on the jobs tables and lock waits (sampled every 100 ms).
/// </summary>
[Collection(PerfCollection.Name)]
[Trait("Category", "Performance")]
public sealed class ContentionTests(PerfDatabaseFixture fixture, ITestOutputHelper output)
{
    private const int Claimers = 8;
    private const int BatchSize = 500;

    public static TheoryData<DatabaseKind> Kinds => PerfDatabaseFixture.AllKinds;

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task No_deadlocks_and_no_lock_escalation_at_batch_size_500(DatabaseKind kind)
    {
        PerfDatabase db = await fixture.CreateDatabaseAsync(kind);
        int seeded = Math.Max(10_000, PerfScale.Jobs * 3 / 2);
        int enqueued = Math.Max(2_000, PerfScale.Jobs / 2);
        await Seeder.SeedPendingAsync(db, NoOpHandler.Code, "default", seeded, readySecondsAgo: 5);
        List<PerfHost> hosts = [.. Enumerable.Range(0, Claimers).Select(i => PerfHost.Build(db, $"perf-claimer-{i}", o => o.Worker.Enabled = false))];
        PerfHost producer = PerfHost.Build(db, "perf-producer", o => o.Worker.Enabled = false);
        long deadlocksBefore = await DbProbes.DeadlocksAsync(db);
        long escalationsBefore = await DbProbes.LockEscalationsAsync(db);
        int clientDeadlocks = 0;
        ConcurrentBag<string> errors = [];
        int maxWaiting = 0;
        int maxTableLocks = 0;
        long waitingSum = 0;
        int samples = 0;
        int completed = 0;
        int maxBatch = 0;
        List<string> tableLockDetails = [];
        using CancellationTokenSource stop = new();
        try
        {
            Task sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    (int waiting, int tableLocks) = await DbProbes.SampleLocksAsync(db);
                    if (tableLocks > 0 && tableLockDetails.Count < 5)
                    {
                        tableLockDetails.Add(await DbProbes.TableLockDetailAsync(db));
                    }

                    maxWaiting = Math.Max(maxWaiting, waiting);
                    maxTableLocks = Math.Max(maxTableLocks, tableLocks);
                    waitingSum += waiting;
                    samples++;
                    await Task.Delay(100, CancellationToken.None);
                }
            });

            bool producing = true;
            Task producers = Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                // Exactly enqueued / 2 per producer, in batches of up to 500 (the last one smaller when it does not divide).
                for (int done = 0; done < enqueued / 2; done += 500)
                {
                    await producer.EnqueueManyAsync([.. Enumerable.Range(0, Math.Min(500, (enqueued / 2) - done)).Select(_ => new NoOpPayload())]);
                }
            })).ContinueWith(_ => producing = false, TaskScheduler.Default);

            Stopwatch watch = Stopwatch.StartNew();
            await Task.WhenAll(hosts.Select(async host =>
            {
                int idle = 0;
                while (idle < 3)
                {
                    IReadOnlyList<ClaimedJob> batch;
                    try
                    {
                        batch = await host.ClaimAsync(NoOpHandler.Code, BatchSize);
                    }
                    catch (Exception ex) when (IsDeadlock(ex))
                    {
                        Interlocked.Increment(ref clientDeadlocks);
                        continue;
                    }

                    if (batch.Count == 0)
                    {
                        // READPAST / SKIP LOCKED can return nothing while another session briefly holds a row: stop only
                        // when the producers are done and no unleased queue row is left.
                        bool drained = !Volatile.Read(ref producing)
                            && await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue WHERE lease_token IS NULL") == 0;
                        idle = drained ? idle + 1 : 0;
                        await Task.Delay(50);
                        continue;
                    }

                    idle = 0;
                    InterlockedMax(ref maxBatch, batch.Count);
                    await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (job, _) =>
                    {
                        try
                        {
                            OutcomeResult result = await host.SucceedAsync(job);
                            if (result == OutcomeResult.Recorded)
                            {
                                Interlocked.Increment(ref completed);
                            }
                            else
                            {
                                errors.Add($"job {job.Id}: outcome {result}");
                            }
                        }
                        catch (Exception ex) when (IsDeadlock(ex))
                        {
                            Interlocked.Increment(ref clientDeadlocks);
                        }
                        catch (Exception ex) when (ex is DbException)
                        {
                            errors.Add(ex.Message);
                        }
                    });
                }
            }));
            await producers;
            TimeSpan took = watch.Elapsed;
            await stop.CancelAsync();
            await sampler;

            long serverDeadlocks = await DbProbes.DeadlocksAsync(db) - deadlocksBefore;
            long escalations = await DbProbes.LockEscalationsAsync(db) - escalationsBefore;
            int total = seeded + (enqueued / 2 * 2);
            PerfReport.Write(output, kind, $"contention: {Claimers} claimers x batch {BatchSize} + 2 enqueuers", $"{completed}/{total} jobs in {took.TotalSeconds:F1} s ({completed / took.TotalSeconds:F0} jobs/s), largest batch {maxBatch}", "reported", true);
            PerfReport.Write(output, kind, "deadlocks (client 1205/40P01, server counter)", $"client {clientDeadlocks}, server {serverDeadlocks}", "0", clientDeadlocks == 0 && serverDeadlocks == 0);
            PerfReport.Write(output, kind, "lock escalations on jobs tables", $"{escalations} {await DbProbes.LockEscalationDetailAsync(db)}", "0", escalations == 0);
            PerfReport.Write(output, kind, "table-level S/X/U/SIX locks on jobs tables (max sampled)", $"{maxTableLocks}", "0", maxTableLocks == 0);
            if (tableLockDetails.Count > 0)
            {
                PerfReport.Note(output, kind, $"table-level locks seen: {string.Join(" | ", tableLockDetails)}");
            }

            PerfReport.Write(output, kind, "lock requests waiting (sampled every 100 ms)", string.Create(CultureInfo.InvariantCulture, $"max {maxWaiting}, mean {(samples == 0 ? 0 : (double)waitingSum / samples):F1} over {samples} samples"), "reported", true);

            if (errors.IsEmpty is false || completed != total)
            {
                List<object?[]> states = await db.QueryAsync("""
                    SELECT b.status, CASE WHEN q.job_id IS NULL THEN 'no-queue-row' WHEN q.lease_token IS NULL THEN 'unleased' ELSE 'leased' END, count(*)
                      FROM ninjaqueue.background_jobs b LEFT JOIN ninjaqueue.job_queue q ON q.job_id = b.id
                     WHERE b.status < 10 GROUP BY b.status, CASE WHEN q.job_id IS NULL THEN 'no-queue-row' WHEN q.lease_token IS NULL THEN 'unleased' ELSE 'leased' END
                    """);
                PerfReport.Note(output, kind, $"contention anomalies: errors [{string.Join("; ", errors.Take(10))}], non-terminal [{string.Join(", ", states.Select(r => $"status {r[0]} {r[1]}: {r[2]}"))}]");
            }

            errors.Should().BeEmpty();
            completed.Should().Be(total, "every claimed job completed exactly once");
            (await db.PendingAsync()).Should().Be(0);
            clientDeadlocks.Should().Be(0);
            serverDeadlocks.Should().Be(0, "§14.8: no deadlocks at batch size ≤ 500");
            escalations.Should().Be(0, "§14.8: no lock escalation at batch size ≤ 500");
            maxTableLocks.Should().Be(0, "no table-level lock is taken on the jobs tables");
        }
        finally
        {
            await stop.CancelAsync();
            await producer.DisposeAsync();
            await PerfHost.DisposeAllAsync(hosts);
            db.ClearPools();
        }
    }

    private static bool IsDeadlock(Exception ex)
        => ex is SqlException { Number: 1205 } || ex is PostgresException { SqlState: "40P01" }
            || (ex.InnerException is { } inner && IsDeadlock(inner));

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}