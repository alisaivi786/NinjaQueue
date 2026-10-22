using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaQueue.EntityFrameworkCore.Data;

/// <summary>
/// Gives the engine its <typeparamref name="TContext"/> instances. The default uses the application's EF Core registration
/// (<c>AddDbContextFactory</c> or <c>AddDbContext</c>); replace it when the application builds its contexts another way.
/// </summary>
/// <typeparam name="TContext">The <see cref="DbContext"/> that maps the job tables.</typeparam>
public interface IJobDbContextProvider<TContext>
    where TContext : DbContext
{
    /// <summary>A new context the engine owns and disposes (claims, outcomes, maintenance, admin).</summary>
    TContext CreateDbContext();

    /// <summary>
    /// The context of the current dependency-injection scope. Fenced steps and dead-letter handlers run their transaction on
    /// it, so a handler that injects <typeparamref name="TContext"/> writes inside the engine's transaction.
    /// </summary>
    TContext GetScopedDbContext();
}

/// <summary>
/// Finds the caller's open transaction for <c>IJobEnqueuer.StageAsync</c> (the transactional outbox). The default returns
/// the current scope's <typeparamref name="TContext"/> when it has a transaction open; replace it when the application
/// keeps its transaction somewhere else (its own unit of work).
/// </summary>
/// <typeparam name="TContext">The <see cref="DbContext"/> that maps the job tables.</typeparam>
public interface IJobTransactionLocator<TContext>
    where TContext : DbContext
{
    /// <summary>The context whose open transaction a staged job joins, or null when the caller has none.</summary>
    TContext? FindTransactionalContext();
}

/// <summary>
/// Optional hook around a fenced step or a dead-letter handler. NinjaQueue calls <see cref="Enter"/> with the context that
/// holds the engine's transaction before the handler runs and disposes the result after it. An application whose
/// repositories open their own unit of work uses it to make them join that context; plain EF Core applications need none.
/// </summary>
public interface IJobTransactionParticipant
{
    /// <summary>Starts sharing <paramref name="transactionalContext"/>; the returned handle (if any) ends it.</summary>
    IDisposable? Enter(DbContext transactionalContext);
}

/// <summary>The default <see cref="IJobDbContextProvider{TContext}"/>: the application's EF Core registration.</summary>
internal sealed class DefaultJobDbContextProvider<TContext>(IServiceProvider services) : IJobDbContextProvider<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext()
    {
        if (services.GetService<IDbContextFactory<TContext>>() is { } factory)
        {
            return factory.CreateDbContext();
        }

        DbContextOptions<TContext> options = services.GetService<DbContextOptions<TContext>>()
            ?? throw new InvalidOperationException(
                $"NinjaQueue cannot create {typeof(TContext).Name}: register it with AddDbContext or AddDbContextFactory, "
                + $"or register an IJobDbContextProvider<{typeof(TContext).Name}>.");
        return ActivatorUtilities.CreateInstance<TContext>(services, options);
    }

    public TContext GetScopedDbContext()
        => services.GetService<TContext>()
            ?? throw new InvalidOperationException(
                $"NinjaQueue needs {typeof(TContext).Name} registered as a scoped service (AddDbContext) for fenced steps and "
                + $"dead-letter handlers, or an IJobDbContextProvider<{typeof(TContext).Name}>.");
}

/// <summary>The default <see cref="IJobTransactionLocator{TContext}"/>.</summary>
internal sealed class DefaultJobTransactionLocator<TContext>(IServiceProvider services) : IJobTransactionLocator<TContext>
    where TContext : DbContext
{
    public TContext? FindTransactionalContext()
    {
        // Inside an engine unit of work (a dead-letter handler or fenced step that enqueues) the engine's session wins.
        if (JobUnitOfWork<TContext>.Current is { Transaction: not null } current)
        {
            return current.DbContext;
        }

        return services.GetService<TContext>() is { } scoped && scoped.Database.CurrentTransaction is not null ? scoped : null;
    }
}