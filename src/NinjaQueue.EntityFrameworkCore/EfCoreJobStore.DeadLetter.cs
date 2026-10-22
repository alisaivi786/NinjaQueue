using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// Dead-letter handlers (plan §18 F7): the marker claim, the handler's writes and the JobDeadLetterHandled event share one
// engine-owned transaction, so the handler's writes commit exactly once.
internal sealed partial class EfCoreJobStore<TContext> : IJobDeadLetterStore
{
    public Task<IReadOnlyList<(long Id, string JobType)>> PendingDeadLettersAsync(
        IReadOnlyList<string> jobTypes, int maxHandlerAttempts, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobTypes);
        if (jobTypes.Count == 0 || batchSize <= 0)
        {
            return Task.FromResult<IReadOnlyList<(long, string)>>([]);
        }

        return unitOfWork.UseAsync<IReadOnlyList<(long, string)>>(async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            await using DbCommand command = JobCommand.Create(session.Connection, null, sql.PendingDeadLetters)
                .With("max_attempts", maxHandlerAttempts, DbType.Int32)
                .With("n", batchSize, DbType.Int32);
            if (sql.IsPostgres)
            {
                command.WithInferred("types", jobTypes.ToArray());
            }
            else
            {
                command.With("types", JsonSerializer.Serialize(jobTypes), DbType.String);
            }

            List<(long, string)> pending = [];
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.ReadLong(0), reader.GetString(1)));
            }

            return pending;
        }, cancellationToken);
    }

    public async Task<DeadLetterRun> RunDeadLetterHandlerAsync(
        long jobId, Func<DeadLetteredJob, CancellationToken, Task>? handler, TimeSpan timeout, int maxHandlerAttempts, CancellationToken cancellationToken)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        try
        {
            DeadLetterRunStatus status = await unitOfWork.UseSharedTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                JobSql sql = Sql(session.DbContext);
                DbConnection connection = session.Connection;
                DbTransaction transaction = session.Transaction!.GetDbTransaction();
                if (handler is not null)
                {
                    EnsureGuardInstalled(session.DbContext);
                }

                // 1. The marker claim: first statement, row lock held to COMMIT. No row = someone else has it (or it is done).
                DeadLetteredJob? job = await ClaimDeadLetterAsync(sql, connection, transaction, jobId, maxHandlerAttempts, ct);
                if (job is null)
                {
                    return DeadLetterRunStatus.NotPending;
                }

                if (handler is null)
                {
                    return DeadLetterRunStatus.Settled;
                }

                if (System.Transactions.Transaction.Current is not null)
                {
                    throw new DeadLetterHandlerFailedException(new FencedContractViolationException(
                        "A System.Transactions TransactionScope is active around a dead-letter handler. It uses only the engine's transaction."));
                }

                int boundMs = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
                if (sql.StepBound(boundMs) is { } stepBound)
                {
                    await JobCommand.Create(connection, transaction, stepBound).RunAsync(ct);
                }

                // 2. The handler, with nested units of work joining this transaction and the guard watching it.
                using (unitOfWork.EnterHandlerScope(session))
                using (FencedTransactionGuard.Enter(connection, transaction))
                {
                    try
                    {
                        await handler(job, ct);
                        await session.DbContext.SaveChangesAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        throw new DeadLetterHandlerFailedException(ex);
                    }
                }

                // 3. The audit event commits with the marker and the handler's writes.
                await InsertEventAsync(sql, connection, transaction, jobId, JobEventCodes.JobDeadLetterHandled, null, null, null, ct);
                return DeadLetterRunStatus.Handled;
            }, bound.Token);
            return new DeadLetterRun(status);
        }
        catch (DeadLetterHandlerFailedException failed)
        {
            // Rolled back: the marker is NULL again. Count the failure in its own transaction.
            Exception error = bound.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                ? new TimeoutException($"The dead-letter handler exceeded its {timeout} bound.", failed.InnerException)
                : failed.InnerException!;
            return await CountDeadLetterFailureAsync(jobId, error, maxHandlerAttempts, cancellationToken);
        }
    }

    private async Task<DeadLetterRun> CountDeadLetterFailureAsync(long jobId, Exception error, int maxHandlerAttempts, CancellationToken cancellationToken)
        => await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection connection = session.Connection;
            DbTransaction transaction = session.Transaction!.GetDbTransaction();
            object? counted;
            await using (DbCommand count = JobCommand.Create(connection, transaction, sql.CountDeadLetterFailure).Id(jobId))
            {
                counted = await count.ExecuteScalarAsync(ct);
            }

            if (counted is null or DBNull)
            {
                return new DeadLetterRun(DeadLetterRunStatus.NotPending, Error: error);
            }

            int attempts = Convert.ToInt32(counted, System.Globalization.CultureInfo.InvariantCulture);
            string reason = $"Attempt {attempts} of {maxHandlerAttempts}: {error.GetType().Name}: {error.Message}";
            await InsertEventAsync(sql, connection, transaction, jobId, JobEventCodes.JobDeadLetterHandlerFailed, null, null, reason, ct);
            if (attempts < maxHandlerAttempts)
            {
                return new DeadLetterRun(DeadLetterRunStatus.Failed, attempts, error);
            }

            await InsertEventAsync(sql, connection, transaction, jobId, JobEventCodes.JobDeadLetterHandlerAbandoned, null, null,
                $"The dead-letter handler failed {attempts} times; it is not retried again.", ct);
            return new DeadLetterRun(DeadLetterRunStatus.Abandoned, attempts, error);
        }, cancellationToken);

    private static async Task<DeadLetteredJob?> ClaimDeadLetterAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, long jobId, int maxHandlerAttempts, CancellationToken ct)
    {
        await using DbCommand claim = JobCommand.Create(connection, transaction, sql.ClaimDeadLetter)
            .Id(jobId)
            .With("max_attempts", maxHandlerAttempts, DbType.Int32);
        await using DbDataReader reader = await claim.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new DeadLetteredJob(
            jobId, new JobId(reader.GetGuid(0)), reader.GetString(1), reader.ReadGuidOrNull(2), reader.ReadInt(3), reader.ReadInt(4),
            reader.ReadStringOrNull(5), reader.ReadStringOrNull(6), reader.ReadStringOrNull(7), reader.ReadStringOrNull(8));
    }

#pragma warning disable CA1032, RCS1194 // Private control-flow exception: only the constructor used here.
    private sealed class DeadLetterHandlerFailedException(Exception inner) : Exception("The dead-letter handler failed.", inner);
#pragma warning restore CA1032, RCS1194
}