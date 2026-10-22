using System.Diagnostics;
using DotNet.Testcontainers.Containers;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// Dedicated PostgreSQL 16 and SQL Server 2022 containers for the G8 outage tests, so pausing a server (<c>docker pause</c>;
/// Testcontainers 4.1 has no pause API) never disturbs the shared <see cref="JobsDatabaseFixture"/> used by every other test.
/// Connection strings carry short command timeouts so calls against a paused server fail in seconds; SQL Server keeps a
/// realistic connect timeout, since right after an unpause it is slow to complete the pre-login handshake.
/// </summary>
public sealed class PausableDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private int _counter;

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _sqlServer.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _sqlServer.DisposeAsync();
    }

    public async Task<TestDatabase> CreateDatabaseAsync(DatabaseKind kind)
    {
        string name = $"g8_{kind.ToString().ToLowerInvariant()}_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}"[..40];
        if (kind == DatabaseKind.Postgres)
        {
            await using (NpgsqlConnection admin = new(_postgres.GetConnectionString()))
            {
                await admin.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE DATABASE {name}", admin);
                await create.ExecuteNonQueryAsync();
            }

            return new TestDatabase(kind, new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            {
                Database = name,
                Timeout = 3,
                CommandTimeout = 5,
                CancellationTimeout = 2000,
            }.ConnectionString);
        }

        await using (SqlConnection master = new(_sqlServer.GetConnectionString()))
        {
            await master.OpenAsync();
            string rcsi = kind == DatabaseKind.SqlServerRcsiOn ? "ON" : "OFF";
            await using SqlCommand create = new($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT {rcsi} WITH ROLLBACK IMMEDIATE;", master);
            await create.ExecuteNonQueryAsync();
        }

        return new TestDatabase(kind, new SqlConnectionStringBuilder(_sqlServer.GetConnectionString())
        {
            InitialCatalog = name,
            TrustServerCertificate = true,
            ConnectTimeout = 15, // SQL Server answers the pre-login handshake only seconds after `docker unpause`
            CommandTimeout = 5,
            ConnectRetryCount = 0,
        }.ConnectionString);
    }

    public Task PauseAsync(DatabaseKind kind) => DockerAsync("pause", Container(kind));

    public Task UnpauseAsync(DatabaseKind kind) => DockerAsync("unpause", Container(kind));

    private IContainer Container(DatabaseKind kind) => kind == DatabaseKind.Postgres ? _postgres : _sqlServer;

    private static async Task DockerAsync(string verb, IContainer container)
    {
        using Process process = Process.Start(new ProcessStartInfo("docker", $"{verb} {container.Id}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("docker could not be started.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {verb} failed: {await process.StandardError.ReadToEndAsync()}");
        }
    }
}