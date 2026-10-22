using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.EntityFrameworkCore.Data;

namespace NinjaQueue.EntityFrameworkCore.Migrations;

/// <summary>
/// A database NinjaQueue runs on. The provider packages (<c>NinjaQueue.PostgreSql</c>, <c>NinjaQueue.SqlServer</c>) register one;
/// the core never references a database driver.
/// </summary>
public interface INinjaQueueDatabaseProvider
{
    /// <summary>True when this provider handles the EF Core provider <paramref name="efProviderName"/> (<c>Database.ProviderName</c>).</summary>
    bool Supports(string? efProviderName);

    /// <summary>Configures <paramref name="optionsBuilder"/> for <see cref="NinjaQueueDbContext"/> on <paramref name="connection"/>,
    /// with the migration history table in <paramref name="schema"/>.</summary>
    void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection, string schema);
}

/// <summary>Installs, scripts and removes the NinjaQueue schema.</summary>
public interface INinjaQueueSchemaMigrator
{
    /// <summary>Applies every pending NinjaQueue migration (EF Core serializes concurrent callers with a database lock).</summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>An idempotent SQL script of every NinjaQueue migration, for environments where a DBA applies schema changes.</summary>
    Task<string> GenerateScriptAsync(CancellationToken cancellationToken = default);

    /// <summary>The applied NinjaQueue migrations.</summary>
    Task<IReadOnlyList<string>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes every NinjaQueue table, the migration history and the schema. The application's own tables are not touched.</summary>
    Task UninstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>Runs NinjaQueue's migrations through a <see cref="NinjaQueueDbContext"/> on the job store's connection.</summary>
internal sealed class NinjaQueueSchemaMigrator<TContext>(
    IJobDbContextProvider<TContext> contexts, IEnumerable<INinjaQueueDatabaseProvider> providers, IOptions<NinjaQueueOptions> options)
    : INinjaQueueSchemaMigrator
    where TContext : DbContext
{
    public Task MigrateAsync(CancellationToken cancellationToken = default)
        => WithMigrationContextAsync((context, ct) => context.Database.MigrateAsync(ct), cancellationToken);

    public Task<string> GenerateScriptAsync(CancellationToken cancellationToken = default)
        => WithMigrationContextAsync((context, _) => Task.FromResult(
            context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent)), cancellationToken);

    public Task<IReadOnlyList<string>> GetAppliedMigrationsAsync(CancellationToken cancellationToken = default)
        => WithMigrationContextAsync<IReadOnlyList<string>>(
            async (context, ct) => [.. await context.Database.GetAppliedMigrationsAsync(ct)], cancellationToken);

    public Task UninstallAsync(CancellationToken cancellationToken = default)
        => WithMigrationContextAsync(async (context, ct) =>
        {
            await context.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase, ct);

            // Identifiers cannot be parameters; the schema is a validated lowercase identifier (JobSchemaName.Validate).
            string schema = JobSchemaName.Validate(context.Schema);
            string dropHistory = "DROP TABLE IF EXISTS " + schema + "." + NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable;
            string dropSchema = "DROP SCHEMA IF EXISTS " + schema;
            await context.Database.ExecuteSqlRawAsync(dropHistory, ct);
            await context.Database.ExecuteSqlRawAsync(dropSchema, ct);
            return true;
        }, cancellationToken);

    private Task WithMigrationContextAsync(Func<NinjaQueueDbContext, CancellationToken, Task> work, CancellationToken cancellationToken)
        => WithMigrationContextAsync(async (context, ct) =>
        {
            await work(context, ct);
            return true;
        }, cancellationToken);

    private async Task<T> WithMigrationContextAsync<T>(Func<NinjaQueueDbContext, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        string schema = JobSchemaName.Validate(options.Value.Schema);

        // The store's own context tells us the database and gives us its connection; the migrations always run through
        // NinjaQueueDbContext so the application's model and migration history are never involved.
        await using TContext source = contexts.CreateDbContext();
        INinjaQueueDatabaseProvider provider = providers.FirstOrDefault(p => p.Supports(source.Database.ProviderName))
            ?? throw new InvalidOperationException(
                $"NinjaQueue has no database provider for '{source.Database.ProviderName}'. Reference NinjaQueue.PostgreSql or "
                + "NinjaQueue.SqlServer and call UsePostgreSql / UseSqlServer on AddNinjaQueue().");

        DbContextOptionsBuilder<NinjaQueueDbContext> builder = new();
        provider.Configure(builder, source.Database.GetDbConnection(), schema);
        builder.UseNinjaQueueSchema(schema);
        await using NinjaQueueDbContext context = new(builder.Options);
        return await work(context, cancellationToken);
    }
}

/// <summary>Applies pending migrations when the host starts (<see cref="NinjaQueueOptions.AutoMigrate"/>), before any worker runs.</summary>
internal sealed partial class NinjaQueueSchemaInitializer(IServiceScopeFactory scopes, IOptions<NinjaQueueOptions> options, ILogger<NinjaQueueSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AutoMigrate)
        {
            return;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        INinjaQueueSchemaMigrator migrator = scope.ServiceProvider.GetRequiredService<INinjaQueueSchemaMigrator>();
        await migrator.MigrateAsync(cancellationToken);
        Migrated(logger, options.Value.Schema);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 7400, Level = LogLevel.Information, Message = "NinjaQueue schema {Schema} is up to date")]
    private static partial void Migrated(ILogger logger, string schema);
}