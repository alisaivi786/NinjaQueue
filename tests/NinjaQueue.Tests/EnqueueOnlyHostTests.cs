using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NinjaQueue.PostgreSql;

namespace NinjaQueue.Tests;

/// <summary>
/// An enqueue-only instance (workers off, e.g. an API whose jobs another service runs) must start and stop without the
/// database: it can come up before the database, and a database outage during a deploy must not break its shutdown.
/// </summary>
public sealed class EnqueueOnlyHostTests
{
    [Fact]
    public async Task Workers_off_starts_and_stops_with_the_database_unreachable()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddNinjaQueue(configure: options =>
        {
            options.AutoMigrate = false;
            options.Worker.Enabled = false;
        }).UsePostgreSql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");

        using IHost host = builder.Build();

        Func<Task> startAndStop = async () =>
        {
            await host.StartAsync();
            await host.StopAsync();
        };

        await startAndStop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Hosted_services_can_be_stopped_after_the_container_disposed_them()
    {
        // WebApplicationFactory may dispose the services before it stops the host.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddNinjaQueue(configure: options =>
        {
            options.AutoMigrate = false;
            options.Worker.Enabled = false;
        }).UsePostgreSql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");

        using IHost host = builder.Build();
        await host.StartAsync();
        List<IHostedService> hostedServices = [.. host.Services.GetServices<IHostedService>()];
        await ((IAsyncDisposable)host.Services).DisposeAsync();

        Func<Task> stop = async () =>
        {
            foreach (IHostedService service in hostedServices)
            {
                await service.StopAsync(CancellationToken.None);
            }
        };

        await stop.Should().NotThrowAsync();
    }
}