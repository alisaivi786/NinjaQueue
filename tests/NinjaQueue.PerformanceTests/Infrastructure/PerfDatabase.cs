using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>A database created for one performance test, with raw-SQL helpers for measurements and seeding.</summary>
public sealed record PerfDatabase(DatabaseKind Kind, string ConnectionString)
{
    public bool IsPostgres => Kind == DatabaseKind.Postgres;

    public string Now => IsPostgres ? "now()" : "SYSDATETIMEOFFSET()";

    /// <summary>Milliseconds from <paramref name="from"/> to <paramref name="to"/> (two timestamp expressions), as a number.</summary>
    public string MsBetween(string from, string to)
        => IsPostgres ? $"(EXTRACT(EPOCH FROM ({to} - {from})) * 1000)" : $"DATEDIFF_BIG(microsecond, {from}, {to}) / 1000.0";

    /// <summary>Reads a numeric column of every row as doubles.</summary>
    public async Task<List<double>> DoublesAsync(string sql)
        => [.. (await QueryAsync(sql)).Select(r => Convert.ToDouble(r[0], CultureInfo.InvariantCulture))];

    /// <summary>Jobs not yet terminal.</summary>
    public Task<int> PendingAsync() => ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.background_jobs WHERE status < 10");

    /// <summary>The same database with its own connection pool and a recognisable session name (application_name / program_name).</summary>
    public string ConnectionStringFor(string applicationName)
        => IsPostgres
            ? new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = applicationName }.ConnectionString
            : new SqlConnectionStringBuilder(ConnectionString) { ApplicationName = applicationName }.ConnectionString;

    public DbConnection CreateConnection() => IsPostgres ? new NpgsqlConnection(ConnectionString) : new SqlConnection(ConnectionString);

    public async Task<DbConnection> OpenAsync()
    {
        DbConnection connection = CreateConnection();
        await connection.OpenAsync();
        return connection;
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 900;
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 900;
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 900;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<object?[]> rows = [];
        while (await reader.ReadAsync())
        {
            object?[] row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Releases this database's idle pooled connections (the containers are shared across the run).</summary>
    public void ClearPools()
    {
        if (IsPostgres)
        {
            NpgsqlConnection.ClearAllPools();
        }
        else
        {
            SqlConnection.ClearAllPools();
        }
    }

    /// <summary>Applies NinjaQueue's EF Core migrations.</summary>
    public void MigrateUp()
    {
        DbContextOptionsBuilder<NinjaQueueDbContext> builder = new();
        if (IsPostgres)
        {
            builder.UseNpgsql(ConnectionString, o => o.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, JobSchemaName.Default));
        }
        else
        {
            builder.UseSqlServer(ConnectionString, o => o.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, JobSchemaName.Default));
        }

        builder.UseNinjaQueueSchema(JobSchemaName.Default);
        using NinjaQueueDbContext context = new(builder.Options);
        context.GetService<IMigrator>().Migrate();
    }
}