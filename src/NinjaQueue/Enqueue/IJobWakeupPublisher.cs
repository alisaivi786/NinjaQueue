namespace NinjaQueue.Enqueue;

/// <summary>
/// Wakes queue workers in other processes when jobs are enqueued (PostgreSQL <c>NOTIFY</c>, Release 2). The in-process
/// <see cref="IJobSignal"/> still wakes this process. Optional: without a registration (or on SQL Server) nothing is sent and
/// workers rely on polling.
/// </summary>
public interface IJobWakeupPublisher
{
    /// <summary>Sends the wake-up inside the caller's open transaction, so it is delivered only if that transaction commits.</summary>
    Task PublishInTransactionAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken);

    /// <summary>Sends the wake-up after the jobs were committed, on its own connection.</summary>
    Task PublishAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken);
}