using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>
/// The test host's persistence, wired the way a plain EF Core application wires it: a context factory plus the scoped
/// context. No persistence library sits in between, so the suite proves NinjaQueue on EF Core alone.
/// </summary>
public static class TestHostPersistence
{
    /// <summary>Registers <see cref="TestHostDbContext"/> (factory + scoped) and the app-side unit of work over <paramref name="db"/>.</summary>
    public static IServiceCollection AddTestHostPersistence(this IServiceCollection services, TestDatabase db, string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(db);
        string target = connectionString ?? db.ConnectionString;
        services.AddDbContextFactory<TestHostDbContext>(options =>
        {
            if (db.IsPostgres)
            {
                options.UseNpgsql(target);
            }
            else
            {
                options.UseSqlServer(target);
            }
        });
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<TestHostDbContext>>().CreateDbContext());
        services.AddScoped<AppUnitOfWork>();
        services.AddScoped<AppSessions>();
        return services;
    }
}

/// <summary>What application code sees of its scoped context: the context, its connection and its open transaction.</summary>
public sealed class AppSession(TestHostDbContext dbContext)
{
    public TestHostDbContext DbContext { get; } = dbContext;

    public DbConnection Connection => DbContext.Database.GetDbConnection();

    public IDbContextTransaction? Transaction => DbContext.Database.CurrentTransaction;
}

/// <summary>The application's view of "the current session": its scoped context (inside a fenced step, the engine's).</summary>
public sealed class AppSessions(TestHostDbContext dbContext)
{
    public AppSession Current => new(dbContext);
}

/// <summary>
/// A typical application unit of work over the scoped context: it joins a transaction that is already open (inside a fenced
/// step or the caller's own transaction) and otherwise commits on its own.
/// </summary>
public sealed class AppUnitOfWork(TestHostDbContext dbContext)
{
    public async Task<T> UseAsync<T>(Func<AppSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        bool opened = dbContext.Database.GetDbConnection().State != ConnectionState.Open;
        if (opened)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            T result = await work(new AppSession(dbContext), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return result;
        }
        finally
        {
            if (opened && dbContext.Database.CurrentTransaction is null)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public Task UseAsync(Func<AppSession, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => UseAsync(ToResult(work), cancellationToken);

    public Task<T> UseTransactionAsync<T>(Func<AppSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        => UseTransactionAsync(IsolationLevel.ReadCommitted, work, cancellationToken);

    public Task UseTransactionAsync(Func<AppSession, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => UseTransactionAsync(IsolationLevel.ReadCommitted, ToResult(work), cancellationToken);

    public Task UseTransactionAsync(IsolationLevel isolationLevel, Func<AppSession, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => UseTransactionAsync(isolationLevel, ToResult(work), cancellationToken);

    public async Task<T> UseTransactionAsync<T>(IsolationLevel isolationLevel, Func<AppSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (dbContext.Database.CurrentTransaction is not null)
        {
            // Joined: whoever opened the transaction commits it.
            T joined = await work(new AppSession(dbContext), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return joined;
        }

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
        T result = await work(new AppSession(dbContext), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static Func<AppSession, CancellationToken, Task<bool>> ToResult(Func<AppSession, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return async (session, ct) =>
        {
            await work(session, ct);
            return true;
        };
    }
}