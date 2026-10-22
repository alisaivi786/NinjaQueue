using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>The three database configurations every guarantee must hold on (§16.6).</summary>
public enum DatabaseKind
{
    Postgres,
    SqlServerRcsiOn,
    SqlServerRcsiOff,
}

/// <summary>
/// One PostgreSQL 16 and one SQL Server 2022 container for the whole run. Each test asks for a fresh, empty database of a
/// given <see cref="DatabaseKind"/>; SQL Server RCSI ON and OFF are two databases in the same container. The tests need
/// Docker and fail without it, like the rest of the integration suites.
/// </summary>
public sealed class JobsDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private int _databaseCounter;

    public static TheoryData<DatabaseKind> AllKinds => [DatabaseKind.Postgres, DatabaseKind.SqlServerRcsiOn, DatabaseKind.SqlServerRcsiOff];

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _sqlServer.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _sqlServer.DisposeAsync();
    }

    /// <summary>Creates a new empty database and returns it.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(DatabaseKind kind, int? sqlServerCompatibilityLevel = null)
    {
        string name = $"jobs_{kind.ToString().ToLowerInvariant()}_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}"[..40];

        if (kind == DatabaseKind.Postgres)
        {
            await using (NpgsqlConnection admin = new(_postgres.GetConnectionString()))
            {
                await admin.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE DATABASE {name}", admin);
                await create.ExecuteNonQueryAsync();
            }

            return new TestDatabase(kind, new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name, Pooling = true }.ConnectionString);
        }

        await using (SqlConnection master = new(_sqlServer.GetConnectionString()))
        {
            await master.OpenAsync();
            string rcsi = kind == DatabaseKind.SqlServerRcsiOn ? "ON" : "OFF";
            string compatibility = sqlServerCompatibilityLevel is { } level ? $"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = {level};" : "";
            await using SqlCommand create = new(
                $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT {rcsi} WITH ROLLBACK IMMEDIATE; {compatibility}", master);
            await create.ExecuteNonQueryAsync();
        }

        return new TestDatabase(kind, new SqlConnectionStringBuilder(_sqlServer.GetConnectionString()) { InitialCatalog = name, TrustServerCertificate = true }.ConnectionString);
    }
}

[CollectionDefinition(Name)]
public sealed class JobsDatabaseCollection : ICollectionFixture<JobsDatabaseFixture>
{
    public const string Name = "NinjaQueue databases";
}