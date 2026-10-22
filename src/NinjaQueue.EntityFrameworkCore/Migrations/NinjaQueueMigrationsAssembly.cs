using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable EF1001 // MigrationsAssembly is EF Core's extension point for creating migration instances.

namespace NinjaQueue.EntityFrameworkCore.Migrations;

/// <summary>Creates NinjaQueue migrations with the context's schema, so one migration set serves any schema name.</summary>
internal sealed class NinjaQueueMigrationsAssembly(
    ICurrentDbContext currentContext,
    IDbContextOptions options,
    IMigrationsIdGenerator idGenerator,
    IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
    : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsAssembly(currentContext, options, idGenerator, logger)
{
    private readonly string _schema = options.FindExtension<NinjaQueueSchemaOptionsExtension>()?.Schema ?? JobSchemaName.Default;

    public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        Migration migration = base.CreateMigration(migrationClass, activeProvider);
        if (migration is NinjaQueueMigration ninjaQueue)
        {
            ninjaQueue.Schema = _schema;
        }

        return migration;
    }
}