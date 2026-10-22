using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NinjaQueue.EntityFrameworkCore.Data;
using NinjaQueue.EntityFrameworkCore.Migrations;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>Plugs the EF Core storage into the engine.</summary>
public static class NinjaQueueEfCoreBuilderExtensions
{
    /// <summary>
    /// Stores jobs in <typeparamref name="TContext"/>, which must map the tables (<c>modelBuilder.ApplyNinjaQueue(schema)</c>),
    /// add the <see cref="FencedTransactionGuard"/> interceptor, and be registered with EF Core (<c>AddDbContext</c> and/or
    /// <c>AddDbContextFactory</c>). Applications that build their contexts another way register their own
    /// <see cref="IJobDbContextProvider{TContext}"/>, <see cref="IJobTransactionLocator{TContext}"/> and, when their
    /// repositories open their own unit of work, an <see cref="IJobTransactionParticipant"/>.
    /// </summary>
    public static NinjaQueueBuilder UseEfCore<TContext>(this NinjaQueueBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The engine's own data access: no dependency on any application persistence library.
        builder.Services.TryAddScoped<IJobDbContextProvider<TContext>, DefaultJobDbContextProvider<TContext>>();
        builder.Services.TryAddScoped<IJobTransactionLocator<TContext>, DefaultJobTransactionLocator<TContext>>();
        builder.Services.TryAddScoped<JobUnitOfWork<TContext>>();

        builder.Services.TryAddScoped<EfCoreJobStore<TContext>>();
        builder.Services.TryAddScoped<IJobStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<IJobMaintenanceStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<IJobAdminStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<IJobAdministrationStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<IJobSideEffectStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<IJobDeadLetterStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());

        builder.Services.TryAddScoped<IJobRecurringStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());
        builder.Services.TryAddScoped<Admin.IJobDashboardStore>(sp => sp.GetRequiredService<EfCoreJobStore<TContext>>());

        // The schema: NinjaQueue installs and upgrades its own tables (AutoMigrate), before any worker starts. The
        // initializer goes right after the registry validator (index 0) and so before the worker host.
        builder.Services.TryAddScoped<INinjaQueueSchemaMigrator, NinjaQueueSchemaMigrator<TContext>>();
        if (!builder.Services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(NinjaQueueSchemaInitializer)))
        {
            builder.Services.Insert(Math.Min(1, builder.Services.Count), ServiceDescriptor.Singleton<IHostedService, NinjaQueueSchemaInitializer>());
        }

        return builder;
    }
}