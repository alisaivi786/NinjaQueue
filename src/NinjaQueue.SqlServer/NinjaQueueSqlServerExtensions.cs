using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore.Migrations;

namespace NinjaQueue.SqlServer;

/// <summary>Runs NinjaQueue on SQL Server (compatibility level 130 or later).</summary>
public static class NinjaQueueSqlServerExtensions
{
    /// <summary>
    /// Stores jobs through NinjaQueue's own context on <paramref name="connectionString"/>: the application's existing
    /// database (the job tables live in their own schema, <c>NinjaQueue:Schema</c>, default <c>ninjaqueue</c>) or a dedicated
    /// one. The schema is installed and upgraded on start (<c>NinjaQueue:AutoMigrate</c>). Jobs are enqueued with
    /// <c>EnqueueAsync</c>; to stage a job inside the application's own transaction, map the tables into the application's
    /// context and use <see cref="UseSqlServer{TContext}"/> instead.
    /// </summary>
    public static NinjaQueueBuilder UseSqlServer(this NinjaQueueBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Services.AddDbContextFactory<NinjaQueueDbContext>((sp, options) =>
        {
            string schema = JobSchemaName.Validate(sp.GetRequiredService<IOptions<NinjaQueueOptions>>().Value.Schema);
            options.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, schema));
            options.UseNinjaQueueSchema(schema);
        });
        builder.Services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<NinjaQueueDbContext>>().CreateDbContext());
        return builder.UseSqlServer<NinjaQueueDbContext>();
    }

    /// <summary>
    /// Stores jobs in the application's <typeparamref name="TContext"/> on SQL Server. The context maps the tables
    /// (<c>modelBuilder.ApplyNinjaQueue(schema)</c>), adds the <see cref="FencedTransactionGuard"/> interceptor and is
    /// registered with <c>AddDbContext</c>/<c>AddDbContextFactory</c>; a job staged with <c>StageAsync</c> then commits or rolls
    /// back with the application's own changes. NinjaQueue still installs and upgrades the tables itself.
    /// </summary>
    public static NinjaQueueBuilder UseSqlServer<TContext>(this NinjaQueueBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEfCore<TContext>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INinjaQueueDatabaseProvider, SqlServerDatabaseProvider>());
        return builder;
    }
}

/// <summary>The SQL Server <see cref="INinjaQueueDatabaseProvider"/>.</summary>
public sealed class SqlServerDatabaseProvider : INinjaQueueDatabaseProvider
{
    /// <inheritdoc />
    public bool Supports(string? efProviderName) => efProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection, string schema)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);
        optionsBuilder.UseSqlServer(connection, sql => sql.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, JobSchemaName.Validate(schema)));
    }
}