using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore.Migrations;
using NinjaQueue.Hosting;

namespace NinjaQueue.PostgreSql;

/// <summary>Runs NinjaQueue on PostgreSQL (13 or later).</summary>
public static class NinjaQueuePostgreSqlExtensions
{
    /// <summary>
    /// Stores jobs through NinjaQueue's own context on <paramref name="connectionString"/>: the application's existing
    /// database (the job tables live in their own schema, <c>NinjaQueue:Schema</c>, default <c>ninjaqueue</c>) or a dedicated
    /// one. The schema is installed and upgraded on start (<c>NinjaQueue:AutoMigrate</c>). Jobs are enqueued with
    /// <c>EnqueueAsync</c>; to stage a job inside the application's own transaction, map the tables into the application's
    /// context and use <see cref="UsePostgreSql{TContext}"/> instead.
    /// </summary>
    public static NinjaQueueBuilder UsePostgreSql(this NinjaQueueBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Services.AddDbContextFactory<NinjaQueueDbContext>((sp, options) =>
        {
            string schema = JobSchemaName.Validate(sp.GetRequiredService<IOptions<NinjaQueueOptions>>().Value.Schema);
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, schema));
            options.UseNinjaQueueSchema(schema);
        });
        builder.Services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<NinjaQueueDbContext>>().CreateDbContext());
        return builder.UsePostgreSql<NinjaQueueDbContext>();
    }

    /// <summary>
    /// Stores jobs in the application's <typeparamref name="TContext"/> on PostgreSQL. The context maps the tables
    /// (<c>modelBuilder.ApplyNinjaQueue(schema)</c>), adds the <see cref="FencedTransactionGuard"/> interceptor and is
    /// registered with <c>AddDbContext</c>/<c>AddDbContextFactory</c>; a job staged with <c>StageAsync</c> then commits or rolls
    /// back with the application's own changes. NinjaQueue still installs and upgrades the tables itself.
    /// </summary>
    public static NinjaQueueBuilder UsePostgreSql<TContext>(this NinjaQueueBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEfCore<TContext>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INinjaQueueDatabaseProvider, PostgreSqlDatabaseProvider>());

        // LISTEN/NOTIFY instant wake-up (inert unless NinjaQueue:ListenNotify:Enabled).
        builder.Services.TryAddScoped<IJobWakeupPublisher, PostgresJobWakeupPublisher<TContext>>();
        builder.Services.TryAddSingleton<PostgresNotifyListener<TContext>>();
        builder.Services.TryAddSingleton<IJobWakeupListener>(sp => sp.GetRequiredService<PostgresNotifyListener<TContext>>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgresNotifyListenerHost<TContext>>());
        return builder;
    }
}

/// <summary>The PostgreSQL <see cref="INinjaQueueDatabaseProvider"/>.</summary>
public sealed class PostgreSqlDatabaseProvider : INinjaQueueDatabaseProvider
{
    /// <inheritdoc />
    public bool Supports(string? efProviderName) => efProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, DbConnection connection, string schema)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connection);
        optionsBuilder.UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(NinjaQueueDbContextOptionsExtensions.MigrationsHistoryTable, JobSchemaName.Validate(schema)));
    }
}