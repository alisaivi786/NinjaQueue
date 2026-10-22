using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using NinjaQueue.EntityFrameworkCore.Migrations;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>
/// NinjaQueue's own context: the job tables in their dedicated schema, nothing else. It runs NinjaQueue's migrations (in any
/// mode) and is the job store's context when the application gives NinjaQueue its own database or connection string
/// (<c>UsePostgreSql(connectionString)</c> / <c>UseSqlServer(connectionString)</c>). An application that wants a staged job to
/// commit with its own data maps the tables into its own context instead (<see cref="NinjaQueueModelBuilderExtensions.ApplyNinjaQueue"/>).
/// </summary>
public sealed class NinjaQueueDbContext : DbContext
{
    private readonly string _schema;

    /// <summary>Creates the context. The schema comes from <see cref="NinjaQueueDbContextOptionsExtensions.UseNinjaQueueSchema"/>.</summary>
    public NinjaQueueDbContext(DbContextOptions<NinjaQueueDbContext> options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _schema = options.FindExtension<NinjaQueueSchemaOptionsExtension>()?.Schema ?? JobSchemaName.Default;
    }

    /// <summary>The schema that holds the job tables.</summary>
    public string Schema => _schema;

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance);

        // Migrations are hand-written and carry the schema at run time (no model snapshot to compare against).
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
#pragma warning disable EF1001 // MigrationsAssembly is EF Core's documented extension point for creating migrations.
        optionsBuilder.ReplaceService<IMigrationsAssembly, NinjaQueueMigrationsAssembly>();
#pragma warning restore EF1001
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyNinjaQueue(_schema, excludeFromMigrations: false);
    }
}