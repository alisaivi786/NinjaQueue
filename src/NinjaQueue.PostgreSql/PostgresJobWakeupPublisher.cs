using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore.Data;
using NinjaQueue.EntityFrameworkCore.Sql;

namespace NinjaQueue.PostgreSql;

/// <summary>
/// Sends <c>NOTIFY</c> on enqueue when <see cref="NinjaQueueListenNotifyOptions.Enabled"/> is set and the provider is PostgreSQL;
/// a no-op otherwise (SQL Server keeps polling plus the in-process signal).
/// </summary>
internal sealed class PostgresJobWakeupPublisher<TContext>(
    IJobTransactionLocator<TContext> transactions, JobUnitOfWork<TContext> unitOfWork, IOptions<NinjaQueueOptions> options) : IJobWakeupPublisher
    where TContext : DbContext
{
    private readonly NinjaQueueOptions _options = options.Value;

    public async Task PublishInTransactionAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (!_options.ListenNotify.Enabled || queues.Count == 0
            || transactions.FindTransactionalContext() is not { } context || context.Database.CurrentTransaction is not { } transaction
            || !PostgresWakeup.IsPostgres(context))
        {
            return;
        }

        await NotifyAsync(new JobSession<TContext>(context), transaction.GetDbTransaction(), queues, cancellationToken);
    }

    public Task PublishAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);
        if (!_options.ListenNotify.Enabled || queues.Count == 0)
        {
            return Task.CompletedTask;
        }

        return unitOfWork.UseAsync(async (session, ct) =>
        {
            if (PostgresWakeup.IsPostgres(session.DbContext))
            {
                await NotifyAsync(session, null, queues, ct);
            }

            return 0;
        }, cancellationToken);
    }

    private async Task NotifyAsync(JobSession<TContext> session, System.Data.Common.DbTransaction? transaction, IReadOnlyCollection<string> queues, CancellationToken ct)
    {
        string channel = PostgresWakeup.Channel(_options.Schema);
        foreach (string queue in queues.Distinct(StringComparer.Ordinal))
        {
            await using System.Data.Common.DbCommand command = JobCommand.Create(session.Connection, transaction, PostgresWakeup.NotifySql);
            command.With("channel", channel, DbType.String).With("queue", queue, DbType.String);
            await command.ExecuteScalarAsync(ct);
        }
    }
}