using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NinjaQueue.EntityFrameworkCore.Data;

/// <summary>
/// The engine's own unit of work over <typeparamref name="TContext"/>.
/// <list type="bullet">
/// <item><see cref="UseAsync{TResult}"/> and <see cref="UseTransactionAsync{TResult}"/> run on a new context the engine owns
/// and disposes, so engine work never shares state with the application's request.</item>
/// <item><see cref="UseSharedTransactionAsync{TResult}"/> runs on the dependency-injection scope's context (fenced steps and
/// dead-letter handlers), so a handler that injects <typeparamref name="TContext"/> writes inside the engine's transaction.</item>
/// <item>Inside <see cref="EnterHandlerScope"/> a nested engine call joins the open session instead of opening its own: it
/// flushes with <c>SaveChangesAsync</c> but never begins, commits or rolls back anything.</item>
/// </list>
/// Transactions run inside the provider's execution strategy; a retry starts from an empty change tracker, and a rollback
/// never replaces the exception that caused it.
/// </summary>
/// <typeparam name="TContext">The <see cref="DbContext"/> that maps the job tables.</typeparam>
internal sealed class JobUnitOfWork<TContext>(IJobDbContextProvider<TContext> contexts, IEnumerable<IJobTransactionParticipant> participants)
    where TContext : DbContext
{
    private static readonly AsyncLocal<JobSession<TContext>?> CurrentSession = new();
    private static readonly AsyncLocal<bool> JoinActive = new();
    private readonly IJobTransactionParticipant[] _participants = [.. participants];

    /// <summary>The session open on the current async flow, or null.</summary>
    public static JobSession<TContext>? Current => CurrentSession.Value;

    /// <summary>Runs <paramref name="action"/> on an open connection without a transaction, then saves tracked changes.</summary>
    public Task<TResult> UseAsync<TResult>(Func<JobSession<TContext>, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryGetJoinable(out JobSession<TContext>? outer)
            ? RunJoinedAsync(outer, action, cancellationToken)
            : RunAsync(action, cancellationToken);
    }

    /// <inheritdoc cref="UseAsync{TResult}"/>
    public Task UseAsync(Func<JobSession<TContext>, CancellationToken, Task> action, CancellationToken cancellationToken)
        => UseAsync(ToResult(action), cancellationToken);

    /// <summary>Runs <paramref name="action"/> in a transaction on a new context; commits when it returns.</summary>
    public Task<TResult> UseTransactionAsync<TResult>(
        IsolationLevel isolationLevel, Func<JobSession<TContext>, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TryGetJoinable(out JobSession<TContext>? outer)
            ? RunJoinedAsync(outer, action, cancellationToken)
            : RunInTransactionAsync(contexts.CreateDbContext(), ownsContext: true, isolationLevel, action, cancellationToken);
    }

    /// <inheritdoc cref="UseTransactionAsync{TResult}"/>
    public Task UseTransactionAsync(IsolationLevel isolationLevel, Func<JobSession<TContext>, CancellationToken, Task> action, CancellationToken cancellationToken)
        => UseTransactionAsync(isolationLevel, ToResult(action), cancellationToken);

    /// <summary>
    /// Runs <paramref name="action"/> in a transaction on the scope's context (<see cref="IJobDbContextProvider{TContext}.GetScopedDbContext"/>),
    /// which the job's handler shares. The context is not disposed: the scope owns it.
    /// </summary>
    public Task<TResult> UseSharedTransactionAsync<TResult>(
        IsolationLevel isolationLevel, Func<JobSession<TContext>, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        TContext shared = contexts.GetScopedDbContext();
        if (shared.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                $"The scoped {typeof(TContext).Name} already has an open transaction; a fenced step or dead-letter handler must start its own.");
        }

        return RunInTransactionAsync(shared, ownsContext: false, isolationLevel, action, cancellationToken);
    }

    /// <summary>
    /// Starts the handler part of a shared transaction: nested engine units of work join <paramref name="session"/>, and every
    /// <see cref="IJobTransactionParticipant"/> is entered. Dispose the result when the handler returns.
    /// </summary>
    public IDisposable EnterHandlerScope(JobSession<TContext> session)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool previous = JoinActive.Value;
        JoinActive.Value = true;
        List<IDisposable> entered = [with(_participants.Length)];
        try
        {
            foreach (IJobTransactionParticipant participant in _participants)
            {
                if (participant.Enter(session.DbContext) is { } handle)
                {
                    entered.Add(handle);
                }
            }
        }
        catch
        {
            new HandlerScope(previous, entered).Dispose();
            throw;
        }

        return new HandlerScope(previous, entered);
    }

    private static bool TryGetJoinable([NotNullWhen(true)] out JobSession<TContext>? outer)
    {
        outer = JoinActive.Value ? CurrentSession.Value : null;
        return outer is not null;
    }

    private static async Task<TResult> RunJoinedAsync<TResult>(
        JobSession<TContext> outer, Func<JobSession<TContext>, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        TResult result = await action(outer, cancellationToken);
        await outer.DbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<TResult> RunAsync<TResult>(Func<JobSession<TContext>, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        await using TContext context = contexts.CreateDbContext();
        JobSession<TContext> session = new(context);
        CurrentSession.Value = session;
        try
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            TResult result = await action(session, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return result;
        }
        finally
        {
            CurrentSession.Value = null;
        }
    }

    private static async Task<TResult> RunInTransactionAsync<TResult>(
        TContext context,
        bool ownsContext,
        IsolationLevel isolationLevel,
        Func<JobSession<TContext>, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        JobSession<TContext> session = new(context);
        CurrentSession.Value = session;
        try
        {
            IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();
            bool firstAttempt = true;
            return await strategy.ExecuteAsync(async () =>
            {
                // A retry re-runs the whole delegate on the same context: drop what the failed attempt staged.
                if (!firstAttempt)
                {
                    context.ChangeTracker.Clear();
                }

                firstAttempt = false;
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
                try
                {
                    TResult result = await action(session, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                catch
                {
                    await RollbackPreservingOriginalAsync(transaction);
                    throw;
                }
            });
        }
        finally
        {
            CurrentSession.Value = null;
            if (ownsContext)
            {
                await context.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Rolls back without letting the rollback replace the exception that caused it: never with the caller's token (often
    /// already cancelled), and a rollback on a broken connection is ignored (disposing the transaction still rolls it back).
    /// </summary>
    private static async Task RollbackPreservingOriginalAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (DbException)
        {
            // Broken connection or server-side abort: the transaction is gone either way.
        }
        catch (InvalidOperationException)
        {
            // Already completed, or its connection closed.
        }
    }

    private static Func<JobSession<TContext>, CancellationToken, Task<bool>> ToResult(Func<JobSession<TContext>, CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return async (session, token) =>
        {
            await action(session, token);
            return true;
        };
    }

    private sealed class HandlerScope(bool previous, List<IDisposable> entered) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = entered.Count - 1; i >= 0; i--)
            {
                entered[i].Dispose();
            }

            JoinActive.Value = previous;
        }
    }
}