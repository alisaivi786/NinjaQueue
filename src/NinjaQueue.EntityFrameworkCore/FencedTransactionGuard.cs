using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>Code inside a fenced step tried to commit, roll back or open a transaction or connection of its own (§17.7).</summary>
public sealed class FencedContractViolationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public FencedContractViolationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public FencedContractViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FencedContractViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Enforces the fenced contract inside a fenced step (§15.1, §17.7). While a step runs, only the engine-owned transaction
/// may exist: starting another transaction, committing or rolling back from handler code, saving changes outside the
/// engine's transaction, or opening a second connection of the host context throws before anything is written. Add it to
/// the host context: <c>optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance)</c>. The engine refuses to run a
/// fenced step on a context without it.
/// </summary>
public sealed class FencedTransactionGuard : DbTransactionInterceptor, ISaveChangesInterceptor, IDbConnectionInterceptor
{
    private static readonly AsyncLocal<FencedScope?> Current = new();

    private FencedTransactionGuard()
    {
    }

    /// <summary>The single instance to register.</summary>
    public static FencedTransactionGuard Instance { get; } = new();

    /// <summary>True while a fenced step runs on this async flow.</summary>
    public static bool IsInFencedScope => Current.Value is { Ended: false };

    /// <summary>Marks the engine-owned connection and transaction for the duration of a step. Synchronous on purpose (AsyncLocal).</summary>
    internal static IDisposable Enter(DbConnection connection, DbTransaction transaction)
    {
        FencedScope? previous = Current.Value;
        FencedScope scope = new(connection, transaction, previous);
        Current.Value = scope;
        return scope;
    }

    /// <inheritdoc />
    public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        Reject("start a transaction");
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        Reject("start a transaction");
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Reject("commit");
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Reject("commit");
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult TransactionRollingBack(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        Reject("roll back");
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Reject("roll back");
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        CheckSave(eventData);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CheckSave(eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        CheckConnection(connection);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        CheckConnection(connection);
        return ValueTask.FromResult(result);
    }

    private static void CheckSave(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (Current.Value is not { Ended: false } scope || eventData.Context is null)
        {
            return;
        }

        DbTransaction? transaction = eventData.Context.Database.CurrentTransaction?.GetDbTransaction();
        if (!ReferenceEquals(transaction, scope.Transaction))
        {
            throw new FencedContractViolationException(
                "SaveChanges ran inside a fenced step outside the engine's transaction (a separate unit of work or DbContext). Fenced handlers write only through the ambient unit of work.");
        }
    }

    private static void CheckConnection(DbConnection connection)
    {
        if (Current.Value is { Ended: false } scope && !ReferenceEquals(connection, scope.Connection))
        {
            throw new FencedContractViolationException(
                "A second connection was opened inside a fenced step. Fenced handlers use only the engine's connection (no IDbContextFactory or new DbContext).");
        }
    }

    private static void Reject(string action)
    {
        if (Current.Value is { Ended: false })
        {
            throw new FencedContractViolationException(
                $"Handler code tried to {action} inside a fenced step. The engine owns the transaction: do not commit, roll back or open transactions in a fenced handler.");
        }
    }

    private sealed class FencedScope(DbConnection connection, DbTransaction transaction, FencedScope? previous) : IDisposable
    {
        public DbConnection Connection { get; } = connection;

        public DbTransaction Transaction { get; } = transaction;

        public bool Ended { get; private set; }

        public void Dispose()
        {
            Ended = true;
            Current.Value = previous;
        }
    }
}