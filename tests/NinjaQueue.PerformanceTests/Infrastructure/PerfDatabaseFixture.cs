using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>The three database configurations every §14.8 target is measured on (§16.6).</summary>
public enum DatabaseKind
{
    Postgres,
    SqlServerRcsiOn,
    SqlServerRcsiOff,
}

/// <summary>
/// One PostgreSQL 16 and one SQL Server 2022 container for the whole performance run. PostgreSQL gets room for several
/// worker pools (<c>max_connections = 500</c>). Each test asks for a fresh database of a given <see cref="DatabaseKind"/>.
/// </summary>
public sealed class PerfDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithCommand("-c", "max_connections=500")
        .Build();

    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private int _databaseCounter;

    public static TheoryData<DatabaseKind> AllKinds => [DatabaseKind.Postgres, DatabaseKind.SqlServerRcsiOn, DatabaseKind.SqlServerRcsiOff];

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _sqlServer.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _sqlServer.DisposeAsync();
    }

    /// <summary>Creates a new database, applies the jobs schema and the business table.</summary>
    public async Task<PerfDatabase> CreateDatabaseAsync(DatabaseKind kind)
    {
        string name = $"perf_{kind.ToString().ToLowerInvariant()}_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}"[..40];
        PerfDatabase db;
        if (kind == DatabaseKind.Postgres)
        {
            await using (NpgsqlConnection admin = new(_postgres.GetConnectionString()))
            {
                await admin.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE DATABASE {name}", admin);
                await create.ExecuteNonQueryAsync();
            }

            db = new PerfDatabase(kind, new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
            {
                Database = name,
                Pooling = true,
                MaxPoolSize = 100,
                CommandTimeout = 600,
            }.ConnectionString);
        }
        else
        {
            await using (SqlConnection master = new(_sqlServer.GetConnectionString()))
            {
                await master.OpenAsync();
                string rcsi = kind == DatabaseKind.SqlServerRcsiOn ? "ON" : "OFF";
                await using SqlCommand create = new(
                    $"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT {rcsi} WITH ROLLBACK IMMEDIATE; " +
                    $"ALTER DATABASE [{name}] SET RECOVERY SIMPLE;", master);
                await create.ExecuteNonQueryAsync();
            }

            db = new PerfDatabase(kind, new SqlConnectionStringBuilder(_sqlServer.GetConnectionString())
            {
                InitialCatalog = name,
                TrustServerCertificate = true,
                MaxPoolSize = 100,
                CommandTimeout = 600,
            }.ConnectionString);
        }

        db.MigrateUp();
        await db.ExecuteAsync(db.IsPostgres
            ? "CREATE TABLE perf_widgets (id bigserial PRIMARY KEY, name varchar(200) NOT NULL)"
            : "CREATE TABLE perf_widgets (id bigint IDENTITY PRIMARY KEY, name nvarchar(200) NOT NULL)");
        return db;
    }
}

[CollectionDefinition(Name)]
public sealed class PerfCollection : ICollectionFixture<PerfDatabaseFixture>
{
    public const string Name = "NinjaQueue performance";
}