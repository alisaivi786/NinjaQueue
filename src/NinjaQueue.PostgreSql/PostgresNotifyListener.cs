using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore.Data;
using Npgsql;

namespace NinjaQueue.PostgreSql;

/// <summary>
/// The PostgreSQL LISTEN side of the wake-up (Release 2, opt-in): one dedicated connection per instance listens on the
/// schema's channel and turns each notification into the in-process <see cref="IJobSignal"/>, so an idle queue loop starts
/// within milliseconds instead of at its next poll. On failure (killed backend, network loss, failover) it reconnects with
/// exponential backoff and jitter, and signals every queue once after each (re)connect so nothing missed while disconnected
/// waits for a poll. Polling never stops: it is the fallback for lost notifications and for delayed jobs. Does nothing on
/// SQL Server, when <see cref="NinjaQueueListenNotifyOptions.Enabled"/> is off, or when this instance runs no workers.
/// </summary>
internal sealed class PostgresNotifyListener<TContext>(
    IServiceScopeFactory scopes, IJobSignal signal, IOptions<NinjaQueueOptions> options, ILogger<PostgresNotifyListener<TContext>> logger)
    : IHostedService, IAsyncDisposable, NinjaQueue.Hosting.IJobWakeupListener
    where TContext : DbContext
{
    private readonly NinjaQueueOptions _options = options.Value;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private int _connections;
    private int _notifications;

    /// <summary>True while the LISTEN connection is open.</summary>
    public bool IsListening { get; private set; }

    /// <summary>How many times the listener has connected (1 + reconnects).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Notifications received.</summary>
    public int Notifications => Volatile.Read(ref _notifications);

    /// <summary>The backend process id of the LISTEN connection (0 while disconnected).</summary>
    public int BackendProcessId { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ListenNotify.Enabled && _options.Worker.Enabled)
        {
            _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        try
        {
            await _loop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown budget ran out; the connection is disposed with the process.
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        TimeSpan delay = _options.ListenNotify.InitialReconnectDelay;
        string channel = PostgresWakeup.Channel(_options.Schema);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                NpgsqlConnection? connection = await CreateConnectionAsync(stop);
                if (connection is null)
                {
                    NotifyLog.NotPostgres(logger);
                    return;
                }

                await using (connection)
                {
                    await connection.OpenAsync(stop);
                    connection.Notification += OnNotification;
                    await using (NpgsqlCommand listen = new($"LISTEN \"{channel}\"", connection))
                    {
                        await listen.ExecuteNonQueryAsync(stop);
                    }

                    BackendProcessId = connection.ProcessID;
                    IsListening = true;
                    Interlocked.Increment(ref _connections);
                    delay = _options.ListenNotify.InitialReconnectDelay;
                    NotifyLog.Listening(logger, channel, connection.ProcessID);

                    // Catch up on anything enqueued while this instance was not listening.
                    foreach (string queue in _options.Worker.Queues.Keys)
                    {
                        signal.Notify(queue);
                    }

                    while (!stop.IsCancellationRequested)
                    {
                        if (!await connection.WaitAsync(_options.ListenNotify.KeepAliveInterval, stop))
                        {
                            // Idle: prove the connection is alive, so a silently dropped socket is found within the interval.
                            await using NpgsqlCommand ping = new("SELECT 1", connection);
                            await ping.ExecuteScalarAsync(stop);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                NotifyLog.Lost(logger, ex, (long)delay.TotalMilliseconds);
            }
            finally
            {
                IsListening = false;
                BackendProcessId = 0;
            }

            try
            {
                // Jitter spreads the reconnects of many instances after a database restart.
                await Task.Delay(delay + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 250)), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(_options.ListenNotify.MaxReconnectDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        Interlocked.Increment(ref _notifications);
        signal.Notify(e.Payload);
    }

    // A clone of the application's own connection (same data source, credentials and settings), so the listener needs no
    // separate configuration. Null when the provider is not PostgreSQL.
    private async Task<NpgsqlConnection?> CreateConnectionAsync(CancellationToken stop)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await using DbContext context = scope.ServiceProvider.GetRequiredService<IJobDbContextProvider<TContext>>().CreateDbContext();
        stop.ThrowIfCancellationRequested();
        return context.Database.GetDbConnection() is NpgsqlConnection connection ? (NpgsqlConnection)((ICloneable)connection).Clone() : null;
    }
}

internal static partial class NotifyLog
{
    [LoggerMessage(EventId = 7300, Level = LogLevel.Information, Message = "NinjaQueue listening on PostgreSQL channel {Channel} (backend {ProcessId})")]
    public static partial void Listening(ILogger logger, string channel, int processId);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Warning, Message = "NinjaQueue LISTEN connection lost; polling continues, reconnecting in {DelayMs} ms")]
    public static partial void Lost(ILogger logger, Exception exception, long delayMs);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Information, Message = "NinjaQueue ListenNotify is enabled but the provider is not PostgreSQL; workers keep polling")]
    public static partial void NotPostgres(ILogger logger);
}

/// <summary>Registers the singleton <see cref="PostgresNotifyListener{TContext}"/> as a hosted service exactly once.</summary>
internal sealed class PostgresNotifyListenerHost<TContext>(PostgresNotifyListener<TContext> listener) : IHostedService
    where TContext : DbContext
{
    public Task StartAsync(CancellationToken cancellationToken) => listener.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => listener.StopAsync(cancellationToken);
}