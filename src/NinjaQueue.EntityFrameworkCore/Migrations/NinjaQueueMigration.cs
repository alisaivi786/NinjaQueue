using Microsoft.EntityFrameworkCore.Migrations;

namespace NinjaQueue.EntityFrameworkCore.Migrations;

/// <summary>
/// Base of every NinjaQueue migration. The schema is not baked into the code: <see cref="NinjaQueueMigrationsAssembly"/>
/// sets <see cref="Schema"/> from the context's options before EF Core runs the migration, so one migration set serves any
/// schema name. Migrations are written by hand against the provider-neutral <see cref="MigrationBuilder"/> API (column types
/// come from the provider's type mapping), so the same set runs on PostgreSQL and SQL Server.
/// A released migration never changes; a schema change ships as a new migration.
/// </summary>
public abstract class NinjaQueueMigration : Migration
{
    /// <summary>The schema that holds the job tables (set before the migration runs).</summary>
    public string Schema { get; internal set; } = JobSchemaName.Default;

    /// <summary>True when the migration runs on PostgreSQL.</summary>
    protected static bool IsPostgres(MigrationBuilder migrationBuilder)
        => migrationBuilder?.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true;

    /// <summary>True when the migration runs on SQL Server.</summary>
    protected static bool IsSqlServer(MigrationBuilder migrationBuilder)
        => migrationBuilder?.ActiveProvider?.Contains("SqlServer", StringComparison.Ordinal) == true;
}