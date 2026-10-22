using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaQueue.Execution;
using NinjaQueue.Observability;
using NinjaQueue.Storage;

namespace NinjaQueue.Hosting;

/// <summary>
/// Renews a standard job's lease every third of the lease from its own DI scope and connection (§14.2, §15.2). Zero rows
/// means the lease is lost: the LeaseLost signal fires at once. A database error is retried until the last confirmed expiry
/// minus the safety margin; then the worker self-fences (fires LeaseLost) because it can no longer prove ownership. A pending
/// cancel fires the AdminCancel signal when running-job cancel is enabled.
/// </summary>
internal sealed class LeaseRenewer(IServiceScopeFactory scopes, NinjaQueueOptions options, ILogger logger)
{
    public async Task RunAsync(ClaimedJob job, JobSignals signals, CancellationToken attemptEnded)
    {
        TimeSpan lease = options.Worker.LeaseDuration;
        TimeSpan interval = (lease - options.Worker.LeaseSafetyMargin) / 3;
        // Measured on this machine's clock from before each request, so it can only under-estimate the database expiry.
        DateTimeOffset localConfirmedUntil = DateTimeOffset.UtcNow + lease - TimeSpan.FromSeconds(2);
        DateTimeOffset? cancelSeenAt = null;

        while (!attemptEnded.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, attemptEnded);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (true)
            {
                // A renewal that hangs (database paused, network partition) is cut off at the self-fence deadline, so
                // the deadline is enforced by a timer rather than only checked after a call returns.
                TimeSpan untilFence = localConfirmedUntil - options.Worker.LeaseSafetyMargin - DateTimeOffset.UtcNow;
                if (untilFence <= TimeSpan.Zero)
                {
                    JobLog.SelfFenced(logger, job.JobId.Value);
                    signals.SignalLeaseLost();
                    return;
                }

                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(attemptEnded);
                deadline.CancelAfter(untilFence);
                try
                {
                    DateTimeOffset requested = DateTimeOffset.UtcNow;
                    FenceResult renewed = await RenewOnceAsync(job, lease, deadline.Token);
                    if (!renewed.Matched)
                    {
                        signals.SignalLeaseLost();
                        return;
                    }

                    localConfirmedUntil = requested + lease;
                    if (renewed.CancelRequestedAtUtc is not null && options.EnableRunningJobCancel && signals.Interruptible)
                    {
                        signals.SignalAdminCancel();
                        cancelSeenAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - cancelSeenAt >= options.Worker.CancelGracePeriod)
                        {
                            // §14.4 step 4: the handler ignored the cancel for the whole grace period. Stop renewing and
                            // self-fence: nothing this worker records afterwards commits, the lease expires, and recovery
                            // sees cancel_requested and ends the job Cancelled (never retried).
                            signals.SignalLeaseLost();
                            return;
                        }
                    }

                    break;
                }
                catch (OperationCanceledException) when (attemptEnded.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or OperationCanceledException)
                {
                    // Cannot confirm either way: retry briefly, and self-fence before the lease could have expired.
                    if (DateTimeOffset.UtcNow >= localConfirmedUntil - options.Worker.LeaseSafetyMargin)
                    {
                        JobLog.SelfFenced(logger, job.JobId.Value);
                        signals.SignalLeaseLost();
                        return;
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), attemptEnded);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
    }

    // One renewal from its own scope. The wait ends at the deadline even when the provider does not honour cancellation
    // (a paused server can swallow the cancel request too); an abandoned call keeps its scope until it finishes, so the
    // DbContext is never disposed under a running command.
    private async Task<FenceResult> RenewOnceAsync(ClaimedJob job, TimeSpan lease, CancellationToken deadline)
    {
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        Task<FenceResult> renewal;
        try
        {
            // On the thread pool: a provider that blocks synchronously while opening a connection to an unresponsive server
            // (seen with SqlClient during G8) must not keep this method from reaching the deadline wait below.
            IJobStore store = scope.ServiceProvider.GetRequiredService<IJobStore>();
            renewal = Task.Run(() => store.RenewAsync(job, lease, deadline), CancellationToken.None);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }

        try
        {
            return await renewal.WaitAsync(deadline);
        }
        finally
        {
            if (renewal.IsCompleted)
            {
                await scope.DisposeAsync();
            }
            else
            {
                _ = DisposeWhenDoneAsync(renewal, scope);
            }
        }
    }

    private static async Task DisposeWhenDoneAsync(Task renewal, AsyncServiceScope scope)
    {
        // The abandoned renewal's result (or failure) is irrelevant; only its scope must be released.
        await renewal.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await scope.DisposeAsync();
    }
}