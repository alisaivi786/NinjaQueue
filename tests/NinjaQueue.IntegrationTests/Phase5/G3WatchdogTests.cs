using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// Gate G3 (§16.1, §16.3, §16.5): a fenced step hung in .NET past its <c>StepTimeout</c> is ended on the server (SQL Server: the
/// watchdog kills the registered session; PostgreSQL: <c>SET LOCAL statement_timeout</c> / <c>idle_in_transaction_session_timeout</c>),
/// the row lock is released, the job is recovered and charged and no business rows remain; the watchdog's identity check never
/// kills a reused or idle session; ~200 fenced steps, killed and timed-out ones included, leave no session state on pooled
/// connections; and SQL Server without <c>ALTER ANY CONNECTION</c> refuses to start unless explicitly allowed (health Degraded).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G3")]
public sealed class G3WatchdogTests(JobsDatabaseFixture fixture)
{
    private const string Fenced = "Test.Fenced.Probe.v1";

    public static TheoryData<DatabaseKind> SqlServerKinds => [DatabaseKind.SqlServerRcsiOn, DatabaseKind.SqlServerRcsiOff];

    private Task<JobsHost> HostAsync(DatabaseKind kind)
        => JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<FencedProbeHandler>();
            b.Services.AddSingleton<FencedProbePlan>();
        });

    private static string SessionIdSql(TestDatabase db) => db.IsPostgres ? "SELECT pg_backend_pid()" : "SELECT @@SPID";

    private static Task<int> KillOverdueAsync(JobsHost host, TimeSpan grace)
        => host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().KillOverdueFencedStepsAsync(grace, CancellationToken.None));

    private static async Task<long> JobRowIdAsync(TestDatabase db, JobId id) => await db.JobColumnAsync<long>(id, "id");

    /// <summary>Takes the job's queue row lock from a second connection; throws if the lock is still held after 3 s.</summary>
    private static Task<int> TakeRowLockAsync(TestDatabase db, long jobId)
        => db.ExecuteAsync(db.IsPostgres
            ? $"SET lock_timeout = '3s'; UPDATE ninjaqueue.job_queue SET lease_owner = lease_owner WHERE job_id = {jobId}"
            : $"SET LOCK_TIMEOUT 3000; UPDATE ninjaqueue.job_queue SET lease_owner = lease_owner WHERE job_id = {jobId}");

    /// <summary>The first attempt writes, then hangs in .NET ignoring its token; later attempts write and complete.</summary>
    private static void HangFirstAttempt(FencedProbePlan plan, TestDatabase db, TaskCompletionSource<int> session, TimeSpan hang)
        => plan.Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                session.TrySetResult(Convert.ToInt32(await step.ScalarAsync(SessionIdSql(db), ct), System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(hang, CancellationToken.None);
            }

            return Step.Complete();
        };

    [Theory]
    [MemberData(nameof(SqlServerKinds))]
    public async Task SqlServer_watchdog_kills_an_overdue_fenced_step_and_the_job_is_recovered_charged(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        TestDatabase db = host.Database;
        TaskCompletionSource<int> session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HangFirstAttempt(host.Get<FencedProbePlan>(), db, session, TimeSpan.FromSeconds(8));
        JobId id = await host.EnqueueAsync(new FencedProbePayload("hung"));
        long jobId = await JobRowIdAsync(db, id);

        Task<ExecutionResult?> run = host.RunOneAsync(Fenced);
        int spid = await session.Task.WaitAsync(TimeSpan.FromSeconds(15));

        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_fenced_steps WHERE job_id = {jobId} AND server_session_id = {spid}"))
            .Should().Be(1, "the step registered its session in autocommit, visible to other instances");
        (await KillOverdueAsync(host, TimeSpan.FromSeconds(30))).Should().Be(0, "not past deadline + grace yet");

        await Task.Delay(TimeSpan.FromSeconds(2.5)); // past the 2 s StepTimeout; the handler still sleeps in .NET
        (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(1);

        (await db.EventsOfAsync(id)).Should().Contain(JobEventCodes.FencedStepKilled);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_fenced_steps WHERE job_id = {jobId}")).Should().Be(0);
        (await TakeRowLockAsync(db, jobId)).Should().Be(1, "the killed transaction released the queue row lock");
        (await db.WidgetCountAsync("hung")).Should().Be(0, "the killed step's write rolled back");
        (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(0, "a repeated kill is a no-op");

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(ExecutionResult.Rescheduled);
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Scheduled);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3), "the timed-out attempt is charged");
        (await db.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.TimedOut);
        (await db.WidgetCountAsync("hung")).Should().Be(0);

        await db.MakeAvailableNowAsync(id);
        (await host.RunOneAsync(Fenced)).Should().Be(ExecutionResult.Succeeded);
        (await db.WidgetCountAsync("hung")).Should().Be(1, "only the successful attempt's write exists");
    }

    [Fact]
    public async Task Postgres_idle_in_transaction_timeout_ends_a_hung_step_on_the_server()
    {
        await using JobsHost host = await HostAsync(DatabaseKind.Postgres);
        TestDatabase db = host.Database;
        TaskCompletionSource<int> session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HangFirstAttempt(host.Get<FencedProbePlan>(), db, session, TimeSpan.FromSeconds(8));
        JobId id = await host.EnqueueAsync(new FencedProbePayload("idle"));
        long jobId = await JobRowIdAsync(db, id);

        Task<ExecutionResult?> run = host.RunOneAsync(Fenced);
        int pid = await session.Task.WaitAsync(TimeSpan.FromSeconds(15));
        long hungAt = Stopwatch.GetTimestamp();

        (await JobProbes.EventuallyAsync(async () => await db.ScalarAsync<int>($"SELECT count(*) FROM pg_stat_activity WHERE pid = {pid}") == 0, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the server terminates the idle-in-transaction backend; no watchdog involved");
        Stopwatch.GetElapsedTime(hungAt).Should().BeLessThan(TimeSpan.FromSeconds(6), "bounded by the 2 s StepTimeout, not by the 8 s hang");
        (await TakeRowLockAsync(db, jobId)).Should().Be(1);
        (await db.WidgetCountAsync("idle")).Should().Be(0);

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(ExecutionResult.Rescheduled);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3));
        (await db.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.TimedOut);
        (await db.WidgetCountAsync("idle")).Should().Be(0);
    }

    [Fact]
    public async Task Postgres_statement_timeout_cancels_a_hung_statement_inside_a_step()
    {
        await using JobsHost host = await HostAsync(DatabaseKind.Postgres);
        TestDatabase db = host.Database;
        string? sqlState = null;
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                try
                {
                    await step.ScalarAsync("SELECT pg_sleep(10)", CancellationToken.None); // only the server can stop it
                }
                catch (PostgresException ex)
                {
                    sqlState = ex.SqlState;
                    throw;
                }
            }

            return Step.Complete();
        };
        JobId id = await host.EnqueueAsync(new FencedProbePayload("stmt"));

        long started = Stopwatch.GetTimestamp();
        (await host.RunOneAsync(Fenced)).Should().Be(ExecutionResult.Rescheduled);

        Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(6), "statement_timeout = StepTimeout (2 s) cancels pg_sleep(10)");
        sqlState.Should().Be(PostgresErrorCodes.QueryCanceled);
        (await db.WidgetCountAsync("stmt")).Should().Be(0);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3));
        (await db.StatusOfAsync(id)).Should().Be(JobStatus.Scheduled);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Watchdog_identity_check_never_kills_a_reused_or_idle_session(DatabaseKind kind)
    {
        await using JobsHost host = await HostAsync(kind);
        TestDatabase db = host.Database;
        JobId id = await host.EnqueueAsync(new FencedProbePayload("victim"));
        long jobId = await JobRowIdAsync(db, id);
        string victimCs = db.IsPostgres
            ? new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString
            : new SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString;

        async Task<Victim> VictimAsync(bool inTransaction)
        {
            DbConnection connection = (db with { ConnectionString = victimCs }).CreateConnection();
            await connection.OpenAsync();
            DbTransaction? transaction = inTransaction ? await connection.BeginTransactionAsync() : null;
            await using (DbCommand write = connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = "INSERT INTO test_widgets (name) VALUES ('victim')";
                await write.ExecuteNonQueryAsync();
            }

            await using DbCommand identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = db.IsPostgres
                ? "SELECT pg_backend_pid(), backend_start, CAST(NULL AS bigint) FROM pg_stat_activity WHERE pid = pg_backend_pid()"
                : "SELECT @@SPID, s.login_time, CAST(CURRENT_TRANSACTION_ID() AS bigint) FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID";
            await using DbDataReader reader = await identity.ExecuteReaderAsync();
            await reader.ReadAsync();
            DateTimeOffset start = reader.GetValue(1) switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
                var other => throw new InvalidCastException(other.GetType().Name),
            };
            return new Victim(connection, transaction, Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), start, reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }

        async Task RegisterAsync(int session, DateTimeOffset start, long? txn)
        {
            await using DbConnection c = await db.OpenAsync();
            await using DbCommand insert = c.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO ninjaqueue.job_fenced_steps (job_id, lease_token, server_session_id, session_start_utc, transaction_id, deadline_utc, owner_worker)
                VALUES ({jobId}, '{Guid.NewGuid()}', {session}, @start, {(txn is null ? "NULL" : txn.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))},
                        {(db.IsPostgres ? "now() - interval '1 minute'" : "DATEADD(minute, -1, SYSDATETIMEOFFSET())")}, 'test')
                """;
            DbParameter p = insert.CreateParameter();
            p.ParameterName = "start";
            p.DbType = System.Data.DbType.DateTimeOffset;
            p.Value = start.ToUniversalTime();
            insert.Parameters.Add(p);
            await insert.ExecuteNonQueryAsync();
        }

        static async Task<bool> AliveAsync(DbConnection connection, DbTransaction? transaction)
        {
            try
            {
                await using DbCommand ping = connection.CreateCommand();
                ping.Transaction = transaction;
                ping.CommandText = "SELECT 1";
                await ping.ExecuteScalarAsync();
                return true;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException)
            {
                return false;
            }
        }

        // 1. Same session id, different session start (the id was reused by a new session): never killed.
        Victim victim = await VictimAsync(inTransaction: true);
        await using (victim.Connection)
        {
            await RegisterAsync(victim.Session, victim.Start.AddDays(-1), victim.Txn);
            (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(0);
            (await AliveAsync(victim.Connection, victim.Transaction)).Should().BeTrue();
            (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_fenced_steps WHERE job_id = {jobId}")).Should().Be(0, "the stale registration is dropped");

            // 2. A different transaction on the same session (SQL Server) / the right session but no open transaction (both).
            if (!db.IsPostgres)
            {
                await RegisterAsync(victim.Session, victim.Start, victim.Txn + 1000);
                (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(0);
                (await AliveAsync(victim.Connection, victim.Transaction)).Should().BeTrue();
            }

            // 3. The exact registered session inside its open transaction: killed, and the kill is recorded.
            await RegisterAsync(victim.Session, victim.Start, victim.Txn);
            (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(1);
            (await AliveAsync(victim.Connection, victim.Transaction)).Should().BeFalse();
            (await db.EventsOfAsync(id)).Should().ContainSingle(e => e == JobEventCodes.FencedStepKilled);
        }

        // A session back in the pool (no open transaction) is never killed.
        Victim idle = await VictimAsync(inTransaction: false);
        await using (idle.Connection)
        {
            await RegisterAsync(idle.Session, idle.Start, null);
            (await KillOverdueAsync(host, TimeSpan.Zero)).Should().Be(0);
            (await AliveAsync(idle.Connection, null)).Should().BeTrue();
        }

        (await db.WidgetCountAsync("victim")).Should().Be(1, "the killed transaction's insert rolled back; the autocommit one stayed");
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Pooled_connections_carry_no_session_state_after_200_fenced_steps_including_killed_and_timed_out(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);

        // PostgreSQL: with No Reset On Close the pool does not DISCARD ALL, so any session-level SET the engine leaked would
        // survive into the next user of the connection. The control below proves that the pool really keeps such state.
        string cs = db.IsPostgres ? new NpgsqlConnectionStringBuilder(db.ConnectionString) { NoResetOnClose = true }.ConnectionString : db.ConnectionString;
        if (db.IsPostgres)
        {
            string control = new NpgsqlConnectionStringBuilder(cs) { ApplicationName = "nkj-control", MaxPoolSize = 1 }.ConnectionString;
            await using (NpgsqlConnection c = new(control))
            {
                await c.OpenAsync();
                await using NpgsqlCommand set = new("SET statement_timeout = 1234", c);
                await set.ExecuteNonQueryAsync();
            }

            await using (NpgsqlConnection c = new(control))
            {
                await c.OpenAsync();
                await using NpgsqlCommand show = new("SHOW statement_timeout", c);
                ((string)(await show.ExecuteScalarAsync())!).Should().Be("1234ms", "control: this pool keeps session state across reuse");
            }
        }

        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o => o.Worker.Enabled = false, cs);
        ConcurrentDictionary<int, byte> stepSessions = new();
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            stepSessions.TryAdd(Convert.ToInt32(await step.ScalarAsync(SessionIdSql(db), ct), System.Globalization.CultureInfo.InvariantCulture), 0);
            await step.Widgets.AddAsync($"{payload.Name}-{context.Checkpoint ?? "start"}", ct);
            if (context.Attempt == 1)
            {
                if (payload.Name.StartsWith("fail", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Injected failure after the write.");
                }

                if (payload.Name.StartsWith("timeout", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None); // returns, but past StepTimeout
                }

                if (payload.Name.StartsWith("killed", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None); // killed meanwhile (watchdog / server)
                }
            }

            return context.Checkpoint is null ? Step.Continue("second") : Step.Complete();
        };

        List<JobId> ids = [];
        foreach ((string prefix, int count) in new[] { ("ok", 95), ("fail", 4), ("timeout", 3), ("killed", 3) })
        {
            for (int i = 0; i < count; i++)
            {
                ids.Add(await host.EnqueueAsync(new FencedProbePayload($"{prefix}{i}")));
            }
        }

        using CancellationTokenSource stopWatchdog = new();
        int kills = 0;
        Task watchdog = Task.Run(async () =>
        {
            while (!stopWatchdog.IsCancellationRequested)
            {
                kills += await host.InScopeAsync(sp => sp.GetRequiredService<IJobMaintenanceStore>().KillOverdueFencedStepsAsync(TimeSpan.Zero, CancellationToken.None));
                await Task.Delay(200, CancellationToken.None);
            }
        });

        long started = Stopwatch.GetTimestamp();
        while (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status < 10") > 0 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(3))
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (await host.RunOneAsync(Fenced) is not null)
                {
                }
            })));
            await Task.Delay(50);
        }

        await stopWatchdog.CancelAsync();
        await watchdog;

        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.background_jobs WHERE status = {(short)JobStatus.Succeeded}")).Should().Be(ids.Count);
        int steps = await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_attempts") + 95 + 10; // second steps of every success
        steps.Should().BeGreaterThanOrEqualTo(200);
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_attempts WHERE outcome = {(short)JobAttemptOutcome.TimedOut}")).Should().Be(6, "the 3 timed-out and 3 killed first attempts");
        (await db.WidgetCountAsync()).Should().Be(ids.Count * 2, "only committed steps left rows: two per job, none from failed, timed-out or killed attempts");
        if (!db.IsPostgres)
        {
            // Each 5 s hang is killed; a 2.5 s "timeout" step may also be caught past its 2 s deadline (grace 0 here).
            kills.Should().BeInRange(3, 6);
            (await db.ScalarAsync<int>($"""
                SELECT count(*) FROM ninjaqueue.job_events e JOIN ninjaqueue.background_jobs b ON b.id = e.job_id
                 WHERE e.event_code = '{JobEventCodes.FencedStepKilled}' AND b.payload LIKE '%killed%'
                """)).Should().Be(3, "each hung session was killed exactly once");
            (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_events WHERE event_code = '{JobEventCodes.FencedStepKilled}'")).Should().Be(kills);
        }

        // 200 follow-up uses of the engine's pool succeed, and none carries leaked state. 8 run at once to draw several connections.
        List<SessionState> states = [];
        for (int i = 0; i < 200; i++)
        {
            states.Add(await ProbeSessionAsync(host, db, null));
        }

        TaskCompletionSource allOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int open = 0;
        states.AddRange(await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ProbeSessionAsync(host, db, async () =>
        {
            if (Interlocked.Increment(ref open) == 8)
            {
                allOpen.TrySetResult();
            }

            await allOpen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        })))));

        states.Select(s => s.Session).Intersect(stepSessions.Keys).Should().NotBeEmpty("the probes really reused connections the fenced steps ran on");
        states.Should().OnlyContain(s => !s.InTransaction, "no pooled connection comes back with an open transaction");
        states.Should().OnlyContain(s => s.IsolationReadCommitted, "the isolation level is the default on every pooled connection");
        states.Should().OnlyContain(s => s.TimeoutsDefault, "no statement/idle/lock timeout lingers on a pooled connection");
    }

    private sealed record Victim(DbConnection Connection, DbTransaction? Transaction, int Session, DateTimeOffset Start, long? Txn);

    private sealed record SessionState(int Session, bool InTransaction, bool IsolationReadCommitted, bool TimeoutsDefault);

    private static Task<SessionState> ProbeSessionAsync(WorkerTestHost host, TestDatabase db, Func<Task>? whileOpen)
        => host.InScopeAsync(sp => sp.GetRequiredService<AppUnitOfWork>().UseAsync(async (session, ct) =>
        {
            DbConnection c = session.Connection;
            if (whileOpen is not null)
            {
                await whileOpen();
            }

            await using DbCommand probe = c.CreateCommand();
            if (db.IsPostgres)
            {
                probe.CommandText = """
                    SELECT pg_backend_pid(), current_setting('statement_timeout'), current_setting('idle_in_transaction_session_timeout'),
                           current_setting('transaction_isolation'), txid_current()
                    """;
                long txid;
                int pid;
                bool timeouts;
                bool isolation;
                await using (DbDataReader r = await probe.ExecuteReaderAsync(ct))
                {
                    await r.ReadAsync(ct);
                    pid = r.GetInt32(0);
                    timeouts = r.GetString(1) == "0" && r.GetString(2) == "0";
                    isolation = r.GetString(3) == "read committed";
                    txid = r.GetInt64(4);
                }

                // Outside a transaction every statement is its own transaction, so a second txid differs.
                await using DbCommand again = c.CreateCommand();
                again.CommandText = "SELECT txid_current()";
                long txid2 = Convert.ToInt64(await again.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
                return new SessionState(pid, txid2 == txid, isolation, timeouts);
            }

            probe.CommandText = "SELECT @@SPID, @@TRANCOUNT, @@LOCK_TIMEOUT, s.transaction_isolation_level FROM sys.dm_exec_sessions s WHERE s.session_id = @@SPID";
            await using DbDataReader reader = await probe.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new SessionState(Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt32(1) != 0,
                Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture) == 2, reader.GetInt32(2) == -1);
        }, CancellationToken.None));

    [Theory]
    [MemberData(nameof(SqlServerKinds))]
    public async Task SqlServer_without_ALTER_ANY_CONNECTION_refuses_fenced_jobs_unless_allowed_and_health_is_then_Degraded(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        string login = $"nkj_app_{Guid.NewGuid():N}"[..20];
        string password = $"Nk!{Guid.NewGuid():N}aA1";
        await db.ExecuteAsync($"""
            CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            ALTER ROLE db_owner ADD MEMBER [{login}];
            """);
        string restricted = new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = password }.ConnectionString;

        await using (WorkerTestHost refused = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o => o.Worker.Enabled = false, restricted))
        {
            (await FluentActions.Awaiting(refused.StartAsync).Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("ALTER ANY CONNECTION").And.Contain("AllowFencedWithoutWatchdog");
        }

        await using (WorkerTestHost allowed = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o =>
        {
            o.Worker.Enabled = false;
            o.Watchdog.AllowFencedWithoutWatchdog = true;
        }, restricted))
        {
            await allowed.StartAsync();
            HealthCheckResult health = await allowed.HealthAsync();
            health.Status.Should().Be(HealthStatus.Degraded);
            health.Description.Should().Contain("without the watchdog");
        }

        await using (WorkerTestHost standardOnly = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o => o.Worker.Enabled = false, restricted))
        {
            await standardOnly.StartAsync();
            (await standardOnly.HealthAsync()).Status.Should().Be(HealthStatus.Healthy, "without fenced types the permission is not needed");
        }

        await using WorkerTestHost privileged = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o => o.Worker.Enabled = false);
        await privileged.StartAsync();
        (await privileged.HealthAsync()).Status.Should().Be(HealthStatus.Healthy, "with the permission the full guarantee holds");
    }
}