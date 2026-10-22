using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>A database created for one test, with small helpers for raw SQL that differs between the providers.</summary>
public sealed record TestDatabase(DatabaseKind Kind, string ConnectionString)
{
    public bool IsPostgres => Kind == DatabaseKind.Postgres;

    /// <summary>The boolean literal for this provider.</summary>
    public string Bool(bool value) => IsPostgres ? (value ? "TRUE" : "FALSE") : (value ? "1" : "0");

    /// <summary>The current database time expression.</summary>
    public string Now => IsPostgres ? "now()" : "SYSDATETIMEOFFSET()";

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
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using DbConnection connection = await OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
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

    /// <summary>NinjaQueue's own context on this database (the context its migrations run through).</summary>
    public NinjaQueueDbContext CreateNinjaQueueContext(string schema = JobSchemaName.Default)
    {
        DbContextOptionsBuilder<NinjaQueueDbContext> builder = new();
        if (IsPostgres)
        {
            builder.UseNpgsql(ConnectionString, o => o.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, schema));
        }
        else
        {
            builder.UseSqlServer(ConnectionString, o => o.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, schema));
        }

        builder.UseNinjaQueueSchema(schema);
        return new NinjaQueueDbContext(builder.Options);
    }

    /// <summary>Applies NinjaQueue's EF Core migrations (up to the latest, or to <paramref name="targetMigration"/>).</summary>
    public void MigrateUp(string? targetMigration = null, string schema = JobSchemaName.Default)
    {
        using NinjaQueueDbContext context = CreateNinjaQueueContext(schema);
        context.GetService<IMigrator>().Migrate(targetMigration);
    }

    /// <summary>Rolls every NinjaQueue migration back (the job tables are dropped; the schema and history table stay).</summary>
    public void MigrateDown(string schema = JobSchemaName.Default)
    {
        using NinjaQueueDbContext context = CreateNinjaQueueContext(schema);
        context.GetService<IMigrator>().Migrate(Migration.InitialDatabase);
    }
}