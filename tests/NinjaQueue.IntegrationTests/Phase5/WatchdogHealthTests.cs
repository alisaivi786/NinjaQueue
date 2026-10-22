using Microsoft.Extensions.Diagnostics.HealthChecks;
using NinjaQueue.Execution;

namespace NinjaQueue.IntegrationTests.Phase5;

/// <summary>
/// §16.3 / §7.6 watchdog health and the PostgreSQL kill path:
/// <list type="bullet">
/// <item>PostgreSQL without the kill permission (not superuser, not in <c>pg_signal_backend</c>) still starts and runs fenced jobs,
/// with health Degraded, the same as the SQL Server opt-out; granting the role makes it Healthy.</item>
/// <item><c>watchdog_unreachable</c>: a watchdog that cannot read the session views turns health Unhealthy after three maintenance
/// intervals, and back once it can.</item>
/// <item>With the server-side bounds lifted for one step, the PostgreSQL watchdog itself terminates the hung backend.</item>
/// </list>
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Gate", "G3")]
public sealed class WatchdogHealthTests(JobsDatabaseFixture fixture)
{
    private const string Fenced = "Test.Fenced.Probe.v1";

    public static TheoryData<DatabaseKind> SqlServerKinds => [DatabaseKind.SqlServerRcsiOn, DatabaseKind.SqlServerRcsiOff];

    [Fact]
    public async Task Postgres_without_pg_signal_backend_runs_fenced_jobs_Degraded_and_is_Healthy_once_granted()
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, DatabaseKind.Postgres);
        string role = $"nkj_app_{Guid.NewGuid():N}"[..20];
        string password = $"Nk{Guid.NewGuid():N}";
        string database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database!;
        await db.ExecuteAsync($"""
            CREATE ROLE {role} LOGIN NOSUPERUSER PASSWORD '{password}';
            GRANT CONNECT ON DATABASE {database} TO {role};
            GRANT USAGE ON SCHEMA ninjaqueue TO {role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ninjaqueue TO {role};
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ninjaqueue TO {role};
            GRANT SELECT, INSERT, UPDATE, DELETE ON test_widgets TO {role};
            GRANT USAGE, SELECT ON SEQUENCE test_widgets_id_seq TO {role};
            """);
        string restricted = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Username = role, Password = password }.ConnectionString;

        await using (WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o => o.Worker.Enabled = false, restricted))
        {
            await host.StartAsync(); // PostgreSQL does not refuse: its server-side timeouts still bound a fenced transaction
            HealthCheckResult health = await host.HealthAsync();
            health.Status.Should().Be(HealthStatus.Degraded);
            health.Description.Should().Contain("without the watchdog");
            health.Data["watchdog_can_kill"].Should().Be(false);

            JobId id = await host.EnqueueAsync(new FencedProbePayload("pg-restricted"));
            (await host.RunOneAsync(Fenced)).Should().Be(ExecutionResult.Succeeded, "fenced jobs run with the reduced guarantee");
            (await db.WidgetCountAsync("pg-restricted")).Should().Be(1);
            (await db.StatusOfAsync(id)).Should().Be(JobStatus.Succeeded);
        }

        await db.ExecuteAsync($"GRANT pg_signal_backend TO {role}");
        await using WorkerTestHost granted = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o => o.Worker.Enabled = false, restricted);
        HealthCheckResult healthy = await granted.HealthAsync();
        healthy.Status.Should().Be(HealthStatus.Healthy, healthy.Description);
        healthy.Data["watchdog_can_kill"].Should().Be(true);
    }

    [Theory]
    [MemberData(nameof(SqlServerKinds))]
    public async Task Watchdog_that_cannot_read_the_session_views_turns_health_Unhealthy_until_it_can(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        string login = $"nkj_wd_{Guid.NewGuid():N}"[..20];
        string password = $"Nk!{Guid.NewGuid():N}aA1";
        await db.ExecuteAsync($"""
            CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;
            CREATE USER [{login}] FOR LOGIN [{login}];
            ALTER ROLE db_owner ADD MEMBER [{login}];
            """);
        string restricted = new SqlConnectionStringBuilder(db.ConnectionString) { UserID = login, Password = password }.ConnectionString;

        // db_owner but no VIEW SERVER STATE: the watchdog cannot read sys.dm_tran_session_transactions, so it can neither confirm
        // nor kill a hung session.
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<FencedProbeHandler>(), o =>
        {
            o.Watchdog.AllowFencedWithoutWatchdog = true;
            o.Worker.MaintenanceInterval = TimeSpan.FromMilliseconds(300);
        }, restricted);
        await host.StartAsync();

        HealthCheckResult unhealthy = default;
        (await JobProbes.EventuallyAsync(async () => (unhealthy = await host.HealthAsync()).Status == HealthStatus.Unhealthy, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("three maintenance intervals without a successful watchdog round");
        unhealthy.Description.Should().StartWith("watchdog_unreachable");
        unhealthy.Data["watchdog_last_ok_utc"].Should().Be("never");

        await db.ExecuteAsync($"USE master; GRANT VIEW SERVER STATE TO [{login}];");
        HealthCheckResult recovered = default;
        (await JobProbes.EventuallyAsync(async () => (recovered = await host.HealthAsync()).Status != HealthStatus.Unhealthy, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the next successful round clears watchdog_unreachable");
        recovered.Status.Should().Be(HealthStatus.Degraded, "still no ALTER ANY CONNECTION, so the reduced guarantee remains");
    }

    [Fact]
    public async Task Postgres_watchdog_terminates_a_hung_step_when_the_server_side_bounds_are_lifted()
    {
        await using JobsHost host = await JobsHost.StartAsync(fixture, DatabaseKind.Postgres, b =>
        {
            b.AddJobHandler<FencedProbeHandler>();
            b.Services.AddSingleton<FencedProbePlan>();
        });
        TestDatabase db = host.Database;
        TaskCompletionSource<int> session = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Get<FencedProbePlan>().Step = async (payload, context, step, ct) =>
        {
            await step.Widgets.AddAsync(payload.Name, ct);
            if (context.Attempt == 1)
            {
                // This test only: lift the engine's SET LOCAL bounds so the server never ends the step and the watchdog must.
                await step.ScalarAsync("SET LOCAL statement_timeout = 0; SET LOCAL idle_in_transaction_session_timeout = 0", ct);
                session.TrySetResult(Convert.ToInt32(await step.ScalarAsync("SELECT pg_backend_pid()", ct), System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None);
            }

            return Step.Complete();
        };
        JobId id = await host.EnqueueAsync(new FencedProbePayload("pg-kill"));
        long jobId = await db.JobColumnAsync<long>(id, "id");

        Task<ExecutionResult?> run = host.RunOneAsync(Fenced);
        int pid = await session.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(3)); // past the 2 s StepTimeout; the bounds the engine set would have fired by now
        (await db.ScalarAsync<int>($"SELECT count(*) FROM pg_stat_activity WHERE pid = {pid} AND xact_start IS NOT NULL"))
            .Should().Be(1, "with the bounds lifted the backend is still open in its transaction");

        (await host.InScopeAsync(sp => sp.GetRequiredService<Storage.IJobMaintenanceStore>().KillOverdueFencedStepsAsync(TimeSpan.Zero, CancellationToken.None)))
            .Should().Be(1);

        (await db.ScalarAsync<int>($"SELECT count(*) FROM pg_stat_activity WHERE pid = {pid}")).Should().Be(0, "the kill was confirmed before it was recorded");
        (await db.EventsOfAsync(id)).Should().ContainSingle(e => e == JobEventCodes.FencedStepKilled);
        (await db.ExecuteAsync($"SET lock_timeout = '3s'; UPDATE ninjaqueue.job_queue SET lease_owner = lease_owner WHERE job_id = {jobId}"))
            .Should().Be(1, "the terminated transaction released the queue row lock");
        (await db.WidgetCountAsync("pg-kill")).Should().Be(0);

        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(ExecutionResult.Rescheduled);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3));
        (await db.AttemptOutcomesAsync(id)).Should().Equal(JobAttemptOutcome.TimedOut);
    }

    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Registration_left_by_an_earlier_lease_does_not_block_the_retry(DatabaseKind kind)
    {
        await using JobsHost host = await JobsHost.StartAsync(fixture, kind, b =>
        {
            b.AddJobHandler<FencedProbeHandler>();
            b.Services.AddSingleton<FencedProbePlan>();
        });
        TestDatabase db = host.Database;
        JobId id = await host.EnqueueAsync(new FencedProbePayload("stale-registration"));
        long jobId = await db.JobColumnAsync<long>(id, "id");

        // What an earlier attempt leaves when the database is down as its step ends: its unregister failed (seen in G8).
        await db.ExecuteAsync($"""
            INSERT INTO ninjaqueue.job_fenced_steps (job_id, lease_token, server_session_id, session_start_utc, transaction_id, deadline_utc, owner_worker)
            VALUES ({jobId}, '{Guid.NewGuid()}', 1, {db.Now}, NULL, {db.Now}, 'crashed-worker')
            """);

        (await host.RunOneAsync(Fenced)).Should().Be(ExecutionResult.Succeeded, "the stale row is replaced, not a duplicate key");
        (await db.WidgetCountAsync("stale-registration")).Should().Be(1);
        (await db.AttemptsOfAsync(id)).Should().Be((1, 1, 3));
        (await db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_fenced_steps WHERE job_id = {jobId}")).Should().Be(0, "the step unregistered itself");
    }
}