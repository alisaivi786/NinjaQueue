using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.Admin;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Execution;
using NinjaQueue.Storage;
using Xunit.Abstractions;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>
/// G11 (§17.1): cancel versus fenced finalization. Whichever transaction locks the <c>background_jobs</c> row first wins, so
/// every race has exactly one winner: Succeeded with the step's business rows and <c>JobCancelTooLate</c>, or Cancelled with
/// none of them. <c>Step.Complete</c>, <c>Step.Continue</c>, non-interruptible steps and the Release 1 queued cancel are raced
/// separately, with randomized delays on both sides, and each zero-row cause is forced on its own.
/// Iterations per configuration and mode: <c>NINJA_JOBS_RACES</c> (default 300; the gate target is 5 000).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "4")]
[Trait("Gate", "G11")]
public sealed class G11CancelArbitrationTests(JobsDatabaseFixture fixture, ITestOutputHelper output)
{
    private const string Race = "Test.P4.Race.v1";
    private const string RaceNonInterruptible = "Test.P4.RaceNonInterruptible.v1";
    private const int Parallelism = 8;
    private static readonly Guid Actor = Guid.Parse("11111111-0000-0000-0000-000000000011");

    private static int Races => int.TryParse(Environment.GetEnvironmentVariable("NINJA_JOBS_RACES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 300;

    private enum Mode
    {
        Complete,
        Continue,
        NonInterruptibleContinue,
        QueuedCancel,
    }

    // ---- the randomized races ----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public Task Complete_step_races_cancel_with_exactly_one_winner(DatabaseKind kind) => RunRacesAsync(kind, Mode.Complete);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public Task Continue_steps_race_cancel_and_a_checkpoint_never_reads_as_completed(DatabaseKind kind) => RunRacesAsync(kind, Mode.Continue);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public Task Non_interruptible_steps_commit_and_the_cancel_applies_after_the_attempt(DatabaseKind kind) => RunRacesAsync(kind, Mode.NonInterruptibleContinue);

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public Task Release_1_queued_cancel_races_claim_and_execution_with_exactly_one_winner(DatabaseKind kind) => RunRacesAsync(kind, Mode.QueuedCancel);

    private async Task RunRacesAsync(DatabaseKind kind, Mode mode)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind, o => o.EnableRunningJobCancel = mode != Mode.QueuedCancel);
        host.Probe.RaceMaxDelayMs = 2;

        // Calibrate the cancel delay window to this machine and provider: about 1.5 x the median uncontended execution
        // (claim excluded), measured after one JIT warm-up run.
        List<double> samples = [];
        for (int i = 0; i < 6; i++)
        {
            JobId warm = await EnqueueAsync(host, mode, "warm");
            ClaimedJob job = (await host.ClaimOneAsync(TypeOf(mode), "warm"))!;
            Stopwatch one = Stopwatch.StartNew();
            (await host.ExecuteAsync(job)).Should().Be(ExecutionResult.Succeeded);
            samples.Add(one.Elapsed.TotalMilliseconds);
            (await host.StatusAsync(warm)).Should().Be(JobStatus.Succeeded);
        }

        int windowMs = Math.Max(4, (int)(samples.Skip(1).Order().ElementAt(2) * 1.5));
        int races = Races;
        Dictionary<string, int> tally = [with(StringComparer.Ordinal)];
        List<string> failures = [];
        int next = -1;
        Stopwatch elapsed = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(slot => Task.Run(async () =>
        {
            string queue = $"r{slot}";
            while (Interlocked.Increment(ref next) < races)
            {
                (string outcome, string? failure) = await RaceOnceAsync(host, mode, queue, windowMs);
                lock (tally)
                {
                    tally[outcome] = tally.GetValueOrDefault(outcome) + 1;
                    if (failure is not null)
                    {
                        failures.Add(failure);
                    }
                }
            }
        })));

        output.WriteLine($"{kind} {mode}: {races} races in {elapsed.Elapsed.TotalSeconds:F1} s, cancel window 0-{windowMs} ms, parallelism {Parallelism}");
        foreach ((string outcome, int count) in tally.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"  {outcome}: {count}");
        }

        failures.Should().BeEmpty();
        tally.Values.Sum().Should().Be(races);
        await JobInvariants.AssertHoldAsync(host.Database);
        (await host.Database.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status < 10")).Should().Be(0, "every raced job reached exactly one terminal state");
    }

    private static async Task<(string Outcome, string? Failure)> RaceOnceAsync(Phase4Host host, Mode mode, string queue, int windowMs)
    {
        JobId id = await EnqueueAsync(host, mode, queue);
        int executeDelay = RandomNumberGenerator.GetInt32(0, 3);
        int cancelDelay = RandomNumberGenerator.GetInt32(0, windowMs + 1);
        ClaimedJob? claimed = mode == Mode.QueuedCancel ? null : (await host.ClaimOneAsync(TypeOf(mode), queue))!;

        Task<ExecutionResult?> execute = Task.Run(async () =>
        {
            await Task.Delay(executeDelay);
            ClaimedJob? job = claimed ?? await host.ClaimOneAsync(TypeOf(mode), queue);
            return job is null ? (ExecutionResult?)null : await host.ExecuteAsync(job);
        });
        Task<CancelResult> cancel = Task.Run(async () =>
        {
            await Task.Delay(cancelDelay);
            return await host.InScopeAsync(sp => sp.GetRequiredService<IJobAdminService>().CancelAsync(id, Actor, "g11 race"));
        });
        await Task.WhenAll(execute, cancel);

        RaceRow row = await RaceRow.ReadAsync(host, id);
        string? failure = Judge(mode, row, await execute, await cancel);
        string outcome = $"{row.Status}{(mode == Mode.Continue && row.Status == JobStatus.Cancelled ? row.StartRows == 1 ? " after checkpoint" : " before checkpoint" : "")} / cancel {await cancel}";
        return (outcome, failure is null ? null : $"{id}: {failure} [{row}; execute {await execute}; cancel {await cancel}]");
    }

    /// <summary>The §17.1 arbitration table; returns why a race result is inconsistent, or null.</summary>
    private static string? Judge(Mode mode, RaceRow r, ExecutionResult? executed, CancelResult cancel)
    {
        int finalStepRows = mode is Mode.Complete or Mode.QueuedCancel ? r.StartRows : r.SecondRows;
        if (r.QueueRows != 0)
        {
            return "a terminal job kept its queue row";
        }

        if (r.Succeeded + r.Cancelled != 1)
        {
            return $"expected exactly one of JobSucceeded/JobCancelled, got {r.Succeeded} and {r.Cancelled}";
        }

        return r.Status switch
        {
            JobStatus.Succeeded => JudgeSucceeded(mode, r, executed, cancel, finalStepRows),
            JobStatus.Cancelled => JudgeCancelled(mode, r, executed, cancel, finalStepRows),
            _ => $"neither winner: status {r.Status}",
        };
    }

    private static string? JudgeSucceeded(Mode mode, RaceRow r, ExecutionResult? executed, CancelResult cancel, int finalStepRows)
    {
        if (r.Succeeded != 1 || finalStepRows != 1 || (mode is Mode.Continue or Mode.NonInterruptibleContinue && r.StartRows != 1))
        {
            return "Succeeded without every step's business rows";
        }

        if (r.Attempt != 1 || r.Charged != 1)
        {
            return "a success must charge exactly one attempt";
        }

        if (executed != ExecutionResult.Succeeded)
        {
            return "the executor did not report the success";
        }

        // The cancel lost: "already completed" + JobCancelTooLate, or (Release 1) refused for a running job, or (non-
        // interruptible) requested and then applied after the attempt, which ended in success: JobCancelTooLate.
        return (mode, cancel) switch
        {
            (_, CancelResult.AlreadyCompleted) when r.TooLate == 1 => null,
            (Mode.QueuedCancel, CancelResult.RunningNotSupported) when r.TooLate == 0 => null,
            // Release 2 reports a cancel of a running non-interruptible attempt as CancelAfterCurrentAttempt (§14.4 step 6).
            (Mode.NonInterruptibleContinue, CancelResult.CancelRequested or CancelResult.CancelAfterCurrentAttempt) when r.TooLate == 1 && r.Requested == 1 => null,
            _ => $"Succeeded with cancel result {cancel} and {r.TooLate} JobCancelTooLate",
        };
    }

    private static string? JudgeCancelled(Mode mode, RaceRow r, ExecutionResult? executed, CancelResult cancel, int finalStepRows)
    {
        if (mode == Mode.NonInterruptibleContinue)
        {
            return "a non-interruptible attempt was cancelled half-way";
        }

        if (finalStepRows != 0)
        {
            return "Cancelled with the final step's business rows committed";
        }

        if (mode == Mode.Continue && r.StartRows == 1 && r.Checkpoint != "second")
        {
            return "a committed first step without its checkpoint";
        }

        if (r.Attempt != 0 || r.Charged != 0)
        {
            return "a cancelled attempt must be refunded";
        }

        if (cancel == CancelResult.AlreadyCompleted)
        {
            return "the cancel said 'already completed' but the job was cancelled";
        }

        return mode == Mode.QueuedCancel
            ? cancel == CancelResult.Cancelled && executed is null && r.Claimed == 0 ? null : "a queued cancel must win before any claim"
            : cancel == CancelResult.CancelRequested && r.Requested == 1 && executed == ExecutionResult.Cancelled ? null : "the running cancel was not requested and honoured";
    }

    // ---- forced zero-row causes and the deterministic Continue cases ---------------------------------------------------

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Cancel_after_a_committed_checkpoint_leaves_the_job_processing_then_cancels_the_next_step(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind, o => o.EnableRunningJobCancel = true);
        JobId viaFence = await host.EnqueueAsync(new RacePayload(2));
        JobId viaFinalization = await host.EnqueueAsync(new RacePayload(2));

        // (a) The admin cancel commits between the steps: the checkpoint stays, the job is not terminal, and the next step's
        //     fence sees the cancel.
        ClaimedJob job = (await host.ClaimOneAsync(Race))!;
        FencedStepOutcome first = await host.InScopeAsync(sp => sp.GetRequiredService<IJobStore>().RunFencedStepAsync(job,
            new FencedStepSettings(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20), true), async (_, ct) =>
            {
                await sp.GetRequiredService<WidgetProvider>().AddAsync($"{viaFence.Value:N}-start", ct);
                return new FencedStepResult(false, "second", 1, 2);
            }, CancellationToken.None));
        first.Status.Should().Be(FencedStepStatus.Continued);
        (await host.InScopeAsync(sp => sp.GetRequiredService<IJobAdminService>().CancelAsync(viaFence, Actor, "between steps")))
            .Should().Be(CancelResult.CancelRequested, "a checkpoint commit never makes the cancel return 'already completed'");
        (await host.StatusAsync(viaFence)).Should().Be(JobStatus.Processing);
        (await host.ExecuteAsync(job with { Checkpoint = "second" })).Should().Be(ExecutionResult.Cancelled);
        await AssertCancelledAfterCheckpointAsync(host, viaFence);

        // (b) The cancel commits while the next step runs (after its fence): its finalization matches 0 rows and rolls back.
        CancelResult? requested = null;
        JobStatus? statusAtCancel = null;
        string? checkpointAtCancel = null;
        host.Probe.During = async (jobId, checkpoint) =>
        {
            if (checkpoint == "second")
            {
                statusAtCancel = await host.StatusAsync(new JobId(jobId));
                checkpointAtCancel = await host.CheckpointAsync(new JobId(jobId));
                requested = await RequestCancelRawAsync(host, jobId) == 1 ? CancelResult.CancelRequested : CancelResult.AlreadyRequested;
            }
        };
        (await host.RunOneAsync(Race)).Should().Be(ExecutionResult.Cancelled);
        statusAtCancel.Should().Be(JobStatus.Processing);
        checkpointAtCancel.Should().Be("second", "the first step's checkpoint was committed before the cancel");
        requested.Should().Be(CancelResult.CancelRequested);
        await AssertCancelledAfterCheckpointAsync(host, viaFinalization);
        await JobInvariants.AssertHoldAsync(host.Database);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Zero_rows_because_the_status_changed_underneath_is_a_state_conflict(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        JobId id = await host.EnqueueAsync(new RacePayload(1));
        host.Probe.During = (jobId, _) => host.Database.ExecuteAsync($"UPDATE ninjaqueue.background_jobs SET status = 2 WHERE guid_id = '{jobId}'");

        (await host.RunOneAsync(Race)).Should().Be(ExecutionResult.StateConflict);

        (await host.WidgetsForAsync(id)).Should().Be(0, "the step rolled back");
        (await host.StatusAsync(id)).Should().Be(JobStatus.Scheduled, "the engine makes no state change on a conflict");
        (await host.EventsAsync(id)).Should().Contain(JobEventCodes.JobStateConflict).And.NotContain([JobEventCodes.JobSucceeded, JobEventCodes.JobCancelled]);
        (await host.AttemptRowsAsync(id)).Should().Equal((1, (short)JobAttemptOutcome.LeaseLost, true));
        (await host.QueueRowAsync(id))!.Value.LeaseToken.Should().NotBeNull("the job is left to recovery and admin review");
        await JobInvariants.AssertHoldAsync(host.Database, JobInvariants.ProcessingIsLeased);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Zero_rows_with_the_lease_gone_writes_nothing(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);

        // (a) The token no longer matches at completion (changed inside the step's own transaction): the completion's
        //     lease-checked queue delete matches 0 rows, the step rolls back, and nothing is recorded.
        JobId atCompletion = await host.EnqueueAsync(new RacePayload(1));
        host.Probe.DuringScoped = async (services, jobId) =>
        {
            AppSession session = services.GetRequiredService<AppSessions>().Current;
            await using DbCommand steal = session.Connection.CreateCommand();
            steal.Transaction = session.Transaction!.GetDbTransaction();
            steal.CommandText = $"UPDATE ninjaqueue.job_queue SET lease_token = '{Guid.NewGuid()}' WHERE job_id = (SELECT id FROM ninjaqueue.background_jobs WHERE guid_id = '{jobId}')";
            (await steal.ExecuteNonQueryAsync()).Should().Be(1);
        };
        (await host.RunOneAsync(Race)).Should().Be(ExecutionResult.LeaseLost);
        (await host.WidgetsForAsync(atCompletion)).Should().Be(0);
        (await host.StatusAsync(atCompletion)).Should().Be(JobStatus.Processing, "the finalization rolled back with the step");
        (await host.EventsAsync(atCompletion)).Should().NotContain([JobEventCodes.JobSucceeded, JobEventCodes.JobCancelled, JobEventCodes.JobStateConflict]);
        host.Probe.DuringScoped = null;

        // (b) The finalization matches 0 rows (a cancel) and, before the classification transaction, another owner takes the
        //     lease: the classification's lease check matches 0 rows, so the engine writes nothing — not even Cancelled.
        int leaseLostClassified = 0;
        for (int i = 0; i < 10 && leaseLostClassified == 0; i++)
        {
            JobId id = await host.EnqueueAsync(new RacePayload(1));
            Guid thief = Guid.NewGuid();
            Task<int> takeOver = Task.FromResult(0);
            host.Probe.During = async (jobId, _) =>
            {
                await RequestCancelRawAsync(host, jobId);
                using (ExecutionContext.SuppressFlow())
                {
                    // Blocks on the step's queue-row lock and commits the moment the step rolls back.
                    takeOver = Task.Run(() => host.Database.ExecuteAsync(
                        $"UPDATE ninjaqueue.job_queue SET lease_token = '{thief}' WHERE job_id = (SELECT id FROM ninjaqueue.background_jobs WHERE guid_id = '{jobId}')"));
                }

                await Task.Delay(100);
            };
            ExecutionResult result = (await host.RunOneAsync(Race))!.Value;
            await takeOver;
            (await host.WidgetsForAsync(id)).Should().Be(0);
            if (result == ExecutionResult.LeaseLost)
            {
                leaseLostClassified++;
                (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "a worker that lost the lease records nothing");
                (await host.QueueRowAsync(id))!.Value.LeaseToken.Should().Be(thief);
                (await host.EventsAsync(id)).Should().NotContain([JobEventCodes.JobCancelled, JobEventCodes.JobSucceeded, JobEventCodes.JobStateConflict]);
            }
            else
            {
                result.Should().Be(ExecutionResult.Cancelled, "the classification confirmed the lease before the take-over");
                (await host.StatusAsync(id)).Should().Be(JobStatus.Cancelled);
            }
        }

        leaseLostClassified.Should().BeGreaterThan(0, "the lease-lost classification was exercised");
        host.Probe.During = null;
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Database_cut_during_the_classification_transaction_self_fences(DatabaseKind kind)
    {
        await using Phase4Host host = await Phase4Host.StartAsync(fixture, kind);
        int selfFenced = 0;
        for (int i = 0; i < 5 && selfFenced == 0; i++)
        {
            JobId id = await host.EnqueueAsync(new RacePayload(1));
            Task<bool> holderTask = Task.FromResult(false);
            host.Probe.During = async (jobId, _) =>
            {
                await RequestCancelRawAsync(host, jobId);
                using (ExecutionContext.SuppressFlow())
                {
                    holderTask = Task.Run(() => HoldRowAndKillTheClassificationAsync(host, jobId));
                }

                await Task.Delay(150);
            };

            ExecutionResult result = (await host.RunOneAsync(Race))!.Value;
            bool killed = await holderTask;
            (await host.WidgetsForAsync(id)).Should().Be(0);
            if (killed)
            {
                result.Should().Be(ExecutionResult.LeaseLost, "ownership could not be confirmed, so the worker self-fences");
                (await host.StatusAsync(id)).Should().Be(JobStatus.Processing, "nothing is written for a lease that could not be confirmed; recovery owns the job");
                (await host.EventsAsync(id)).Should().NotContain([JobEventCodes.JobCancelled, JobEventCodes.JobSucceeded, JobEventCodes.JobStateConflict]);
                selfFenced++;
            }
            else
            {
                result.Should().Be(ExecutionResult.Cancelled, "the classification confirmed the lease before the cut could happen");
                (await host.StatusAsync(id)).Should().Be(JobStatus.Cancelled);
            }
        }

        selfFenced.Should().BeGreaterThan(0, "the ownership-unconfirmed classification was exercised");
        host.Probe.During = null;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static string TypeOf(Mode mode) => mode == Mode.NonInterruptibleContinue ? RaceNonInterruptible : Race;

    private static Task<JobId> EnqueueAsync(Phase4Host host, Mode mode, string queue) => mode switch
    {
        Mode.Complete or Mode.QueuedCancel => host.EnqueueAsync(new RacePayload(1), new JobOptions { Queue = queue }),
        Mode.Continue => host.EnqueueAsync(new RacePayload(2), new JobOptions { Queue = queue }),
        _ => host.EnqueueAsync(new RaceNonInterruptiblePayload(2), new JobOptions { Queue = queue }),
    };

    /// <summary>The engine's own admin "request cancel" statement (§17.1), from a second connection.</summary>
    private static async Task<int> RequestCancelRawAsync(Phase4Host host, Guid jobId)
    {
        JobSql sql = host.Database.IsPostgres ? new PostgresJobSql(JobSchemaName.Default) : new SqlServerJobSql(JobSchemaName.Default);
        await using DbConnection connection = await host.Database.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql.RequestCancel;
        command.Parameters.Add(Param(command, "id", await host.DbIdAsync(new JobId(jobId)), System.Data.DbType.Int64));
        command.Parameters.Add(Param(command, "actor_id", Actor, System.Data.DbType.Guid));
        command.Parameters.Add(Param(command, "reason", "g11", System.Data.DbType.String));
        return await command.ExecuteNonQueryAsync();
    }

    private static DbParameter Param(DbCommand command, string name, object value, System.Data.DbType type)
    {
        DbParameter p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        p.DbType = type;
        return p;
    }

    /// <summary>
    /// Waits for the step to roll back, takes the queue-row lock before the classification transaction can, waits until that
    /// transaction blocks on it, kills the classification's session (the database "cut"), then releases the row.
    /// </summary>
    private static async Task<bool> HoldRowAndKillTheClassificationAsync(Phase4Host host, Guid jobId)
    {
        TestDatabase db = host.Database;
        long id = await host.DbIdAsync(new JobId(jobId));
        await using DbConnection holder = await db.OpenAsync();
        await using DbTransaction transaction = await holder.BeginTransactionAsync();
        int holderSession;
        await using (DbCommand who = holder.CreateCommand())
        {
            who.Transaction = transaction;
            who.CommandText = db.IsPostgres ? "SELECT pg_backend_pid()" : "SELECT @@SPID";
            holderSession = Convert.ToInt32(await who.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        await using (DbCommand take = holder.CreateCommand())
        {
            take.Transaction = transaction;
            take.CommandText = db.IsPostgres
                ? $"SELECT job_id FROM ninjaqueue.job_queue WHERE job_id = {id} FOR UPDATE"
                : $"SELECT job_id FROM ninjaqueue.job_queue WITH (UPDLOCK, ROWLOCK) WHERE job_id = {id}";
            if (await take.ExecuteScalarAsync() is null) // blocks until the step transaction rolls back
            {
                return false; // the classification ran first and already ended the job
            }
        }

        string blocked = db.IsPostgres
            ? $"SELECT pid FROM pg_stat_activity WHERE {holderSession} = ANY(pg_blocking_pids(pid))"
            : $"SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id = {holderSession}";
        bool killed = false;
        for (int waited = 0; waited < 10_000 && !killed; waited += 20)
        {
            if (await db.ScalarAsync<int?>(blocked) is { } victim)
            {
                await db.ExecuteAsync(db.IsPostgres ? $"SELECT pg_terminate_backend({victim})" : $"KILL {victim}");
                killed = true;
            }
            else
            {
                await Task.Delay(20);
            }
        }

        await transaction.RollbackAsync();
        return killed;
    }

    private static async Task AssertCancelledAfterCheckpointAsync(Phase4Host host, JobId id)
    {
        (await host.StatusAsync(id)).Should().Be(JobStatus.Cancelled);
        (await host.WidgetsForAsync(id, "start")).Should().Be(1, "the checkpointed step stays committed");
        (await host.WidgetsForAsync(id, "second")).Should().Be(0, "nothing from the cancelled step");
        (await host.CheckpointAsync(id)).Should().Be("second");
        List<string> events = await host.EventsAsync(id);
        events.Should().ContainSingle(e => e == JobEventCodes.JobCancelled).And.NotContain([JobEventCodes.JobSucceeded, JobEventCodes.JobCancelTooLate]);
        (await host.AttemptsAsync(id)).Should().Be((0, 0, 3));
        (await host.QueueRowAsync(id)).Should().BeNull();
    }

    private sealed record RaceRow(
        JobStatus Status, int Attempt, int Charged, int StartRows, int SecondRows, int Succeeded, int Cancelled, int TooLate, int Requested,
        int Claimed, int QueueRows, string? Checkpoint)
    {
        public static async Task<RaceRow> ReadAsync(Phase4Host host, JobId id)
        {
            TestDatabase db = host.Database;
            string n = id.Value.ToString("N");
            string Events(string code) => $"(SELECT count(*) FROM ninjaqueue.job_events e WHERE e.job_id = b.id AND e.event_code = '{code}')";
            object?[] r = (await db.QueryAsync($"""
                SELECT b.status, b.attempt,
                       (SELECT count(*) FROM ninjaqueue.job_attempts a WHERE a.job_id = b.id AND a.charged = {db.Bool(true)}),
                       (SELECT count(*) FROM test_widgets w WHERE w.name = '{n}-start'),
                       (SELECT count(*) FROM test_widgets w WHERE w.name = '{n}-second'),
                       {Events(JobEventCodes.JobSucceeded)}, {Events(JobEventCodes.JobCancelled)}, {Events(JobEventCodes.JobCancelTooLate)},
                       {Events(JobEventCodes.JobCancelRequested)}, {Events(JobEventCodes.JobClaimed)},
                       (SELECT count(*) FROM ninjaqueue.job_queue q WHERE q.job_id = b.id),
                       b.{(db.IsPostgres ? "checkpoint" : "[checkpoint]")}
                  FROM ninjaqueue.background_jobs b WHERE b.guid_id = '{id.Value}'
                """)).Single();
            return new RaceRow((JobStatus)Convert.ToInt16(r[0], CultureInfo.InvariantCulture), Phase4Host.Int(r[1]), Phase4Host.Int(r[2]), Phase4Host.Int(r[3]),
                Phase4Host.Int(r[4]), Phase4Host.Int(r[5]), Phase4Host.Int(r[6]), Phase4Host.Int(r[7]), Phase4Host.Int(r[8]), Phase4Host.Int(r[9]),
                Phase4Host.Int(r[10]), r[11] as string);
        }
    }
}