using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NinjaQueue.IntegrationTests.Phase6;

/// <summary>
/// §7.6 health with the Release 2 LISTEN/NOTIFY wake-up: on PostgreSQL with <c>NinjaQueue:ListenNotify:Enabled</c>, a listener
/// that is not listening (its backend was terminated and it waits to reconnect) reports Degraded with the reason, never
/// Unhealthy, because polling keeps running jobs; after the reconnect the check is Healthy again. On SQL Server the option is
/// inert and does not affect health.
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Phase", "6a")]
public sealed class ListenNotifyHealthTests(JobsDatabaseFixture fixture)
{
    [Theory]
    [MemberData(nameof(JobsDatabaseFixture.AllKinds), MemberType = typeof(JobsDatabaseFixture))]
    public async Task Listener_that_is_not_listening_is_Degraded_while_polling_keeps_jobs_running(DatabaseKind kind)
    {
        TestDatabase db = await WorkerTestHost.CreateDatabaseAsync(fixture, kind);
        await using WorkerTestHost host = WorkerTestHost.Create(db, b => b.AddJobHandler<ProbeHandler>(), o =>
        {
            o.ListenNotify.Enabled = true;
            o.ListenNotify.KeepAliveInterval = TimeSpan.FromSeconds(1);
            o.ListenNotify.InitialReconnectDelay = TimeSpan.FromSeconds(6);
            o.ListenNotify.MaxReconnectDelay = TimeSpan.FromSeconds(6);
        });
        await host.StartAsync();

        if (!db.IsPostgres)
        {
            HealthCheckResult sqlServer = await host.HealthAsync();
            sqlServer.Status.Should().Be(HealthStatus.Healthy, "ListenNotify is inert on SQL Server");
            sqlServer.Data.Should().NotContainKey("listen_notify_listening");
            return;
        }

        PostgresNotifyListener<TestHostDbContext> listener = host.Get<PostgresNotifyListener<TestHostDbContext>>();
        HealthCheckResult health = default;
        (await JobProbes.EventuallyAsync(async () => (health = await host.HealthAsync()).Status == HealthStatus.Healthy, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the listener connects shortly after start");
        health.Data["listen_notify_listening"].Should().Be(true);

        // The LISTEN backend dies (failover, killed session): the listener waits 6 s before reconnecting.
        int pid = listener.BackendProcessId;
        pid.Should().BePositive();
        (await db.ScalarAsync<bool>($"SELECT pg_terminate_backend({pid})")).Should().BeTrue();

        (await JobProbes.EventuallyAsync(async () => (health = await host.HealthAsync()).Status != HealthStatus.Healthy, TimeSpan.FromSeconds(5)))
            .Should().BeTrue();
        health.Status.Should().Be(HealthStatus.Degraded, "polling still works, so a lost listener is never Unhealthy");
        health.Description.Should().Contain("LISTEN/NOTIFY wake-up is not listening");
        health.Data["listen_notify_listening"].Should().Be(false);

        JobId polled = await host.EnqueueAsync(new ProbePayload("polled"));
        (await JobProbes.EventuallyAsync(async () => await db.StatusOfAsync(polled) == JobStatus.Succeeded, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("while not listening, polling runs the job");
        listener.IsListening.Should().BeFalse("the job ran during the reconnect delay, by polling");

        (await JobProbes.EventuallyAsync(async () => (health = await host.HealthAsync()).Status == HealthStatus.Healthy, TimeSpan.FromSeconds(20)))
            .Should().BeTrue("Healthy again after the reconnect");
        listener.BackendProcessId.Should().NotBe(pid);
    }
}