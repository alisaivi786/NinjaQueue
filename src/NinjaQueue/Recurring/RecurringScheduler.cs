using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;
using NinjaQueue.Storage;

namespace NinjaQueue.Recurring;

/// <summary>
/// The recurring scheduler's public surface (§14.6, §15.8): the validated schedules defined in code, a sync of them into
/// the database and an immediate tick. The hosted scheduler calls the same methods on its own timer; hosts and tests call
/// them to act now (after a deploy, in an integration test) instead of waiting for the next tick. Registered when the host
/// adds at least one recurring job. Safe to call on every instance at once.
/// </summary>
public interface IRecurringScheduler
{
    /// <summary>The validated definitions as rows. Throws <see cref="JobRegistryException"/> listing every invalid schedule.</summary>
    IReadOnlyList<RecurringDefinitionRow> Definitions { get; }

    /// <summary>Upserts this instance's schedules (§14.6): inserts new codes, updates changed definitions, disables removed ones.</summary>
    Task<RecurringSyncResult> SyncAsync(CancellationToken cancellationToken);

    /// <summary>Fires every schedule that is due now (bounded per call) and wakes the queues it enqueued on.</summary>
    Task<RecurringTickResult> TickAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Syncs the schedules defined in code and fires due ones (§14.6, §15.8). Provider-neutral: the cron and misfire math run
/// here, the atomic lock-enqueue-advance runs in <see cref="IJobRecurringStore"/>. Safe to run on every instance at once.
/// </summary>
internal sealed class RecurringScheduler(
    IEnumerable<RecurringScheduleDefinition> definitions,
    JobHandlerRegistry registry,
    JobPayloadSerializer serializer,
    IServiceScopeFactory scopes,
    IJobSignal signal,
    IOptions<NinjaQueueOptions> options,
    ILogger<RecurringScheduler> logger) : IRecurringScheduler
{
    /// <summary>The prefix of every recurring idempotency and unique key.</summary>
    public const string KeyPrefix = "recurring:";

    private readonly NinjaQueueRecurringOptions _options = options.Value.Recurring;
    private readonly ConcurrentDictionary<(string Cron, string Zone), CronSchedule> _schedules = new();
    private IReadOnlyList<RecurringDefinitionRow>? _rows;

    /// <summary>The validated definitions as rows. Throws <see cref="JobRegistryException"/> listing every invalid schedule.</summary>
    public IReadOnlyList<RecurringDefinitionRow> Definitions => _rows ??= Build();

    /// <summary>The unique key that blocks overlapping occurrences of <paramref name="code"/>.</summary>
    public static string UniqueKeyFor(string code) => KeyPrefix + code;

    /// <summary>The idempotency key of one occurrence of <paramref name="code"/>.</summary>
    public static string IdempotencyKeyFor(string code, DateTimeOffset occurrenceUtc)
        => $"{KeyPrefix}{code}:{occurrenceUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}";

    /// <summary>Validates every definition (called at host start).</summary>
    public void Validate() => _ = Definitions;

    /// <summary>Upserts this instance's schedules (§14.6).</summary>
    public async Task<RecurringSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RecurringDefinitionRow> rows = Definitions;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        RecurringSyncResult result = await scope.ServiceProvider.GetRequiredService<IJobRecurringStore>().SyncAsync(
            rows, (row, now) => ScheduleOf(row.Cron, row.TimeZone).NextAfter(now), _options.DisableRemovedSchedules && rows.Count > 0, cancellationToken);
        RecurringLog.Synced(logger, result.Inserted, result.Updated, result.Unchanged, result.SkippedNewerStored, result.Disabled);
        return result;
    }

    /// <summary>
    /// Fires every due schedule (bounded per call) and wakes the queues it enqueued on. Returns the combined result.
    /// </summary>
    public async Task<RecurringTickResult> TickAsync(CancellationToken cancellationToken)
    {
        int schedules = 0, enqueued = 0, skipped = 0, deduplicated = 0, failed = 0;
        HashSet<string> queues = [with(StringComparer.Ordinal)];
        List<(string Code, Exception Error)> failures = [];
        for (int round = 0; round < 10; round++)
        {
            RecurringTickResult result;
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                result = await scope.ServiceProvider.GetRequiredService<IJobRecurringStore>()
                    .FireDueAsync(Math.Max(1, _options.MaxSchedulesPerTick), Plan, cancellationToken);
            }

            schedules += result.Schedules;
            enqueued += result.Enqueued;
            skipped += result.SkippedOverlap;
            deduplicated += result.Deduplicated;
            failed += result.Failed;
            queues.UnionWith(result.Queues);
            failures.AddRange(result.Failures);
            foreach ((string code, Exception error) in result.Failures)
            {
                RecurringLog.ScheduleFailed(logger, error, code);
            }

            if (result.Schedules + result.Failed < _options.MaxSchedulesPerTick)
            {
                break;
            }
        }

        foreach (string queue in queues)
        {
            signal.Notify(queue);
        }

        if (schedules > 0 || failed > 0)
        {
            RecurringLog.Ticked(logger, schedules, enqueued, skipped, deduplicated, failed);
        }

        return new RecurringTickResult(schedules, enqueued, skipped, deduplicated, failed, [.. queues]) { Failures = failures };
    }

    /// <summary>Plans one locked, due schedule; null when this instance does not know its job type (rolling deploy).</summary>
    internal RecurringFire? Plan(DueRecurringSchedule due)
    {
        if (!registry.TryGetByCode(due.JobType, out JobTypeRegistration? registration) || registration.Tenancy != JobTenancy.System)
        {
            RecurringLog.UnknownJobType(logger, due.Code, due.JobType);
            return null;
        }

        CronSchedule schedule = ScheduleOf(due.Cron, due.TimeZone);
        RecurringPlan plan = RecurringPlanner.Plan(schedule, due.NextRunAtUtc, due.DatabaseNow, due.MisfirePolicy, _options.MisfireThreshold, _options.FireAllCap);
        if (plan.Truncated)
        {
            RecurringLog.FireAllTruncated(logger, due.Code, _options.FireAllCap);
        }

        string? uniqueKey = due.AllowOverlap ? null : UniqueKeyFor(due.Code);
        return new RecurringFire(
            [.. plan.Fire.Select(occurrence => new RecurringOccurrenceJob(occurrence, new NewJob(
                Guid.NewGuid(), registration.Code, due.Queue, null, JobLimits.SystemTenantScope, due.Payload ?? "null",
                IdempotencyKeyFor(due.Code, occurrence), IdempotencyConflict.ReturnExisting,
                uniqueKey, UniqueKeyConflict.ReturnExisting, registration.MaxAttempts,
                null, null, null, null, null, "recurring")))],
            plan.NextRunAtUtc);
    }

    private CronSchedule ScheduleOf(string cron, string zone) => _schedules.GetOrAdd((cron, zone), k => CronSchedule.Parse(k.Cron, k.Zone));

    private List<RecurringDefinitionRow> Build()
    {
        List<string> errors = [];
        List<RecurringDefinitionRow> rows = [];
        foreach (RecurringScheduleDefinition definition in definitions)
        {
            if (!registry.TryGetByPayload(definition.PayloadType, out JobTypeRegistration? registration))
            {
                errors.Add($"Recurring '{definition.Code}': no job type is registered for payload {definition.PayloadType.FullName}.");
                continue;
            }

            if (registration.Tenancy != JobTenancy.System)
            {
                errors.Add($"Recurring '{definition.Code}': '{registration.Code}' is tenant-owned. Schedule a System job that fans out per tenant with EnqueueManyAsync (§14.6).");
                continue;
            }

            string queue = definition.Options.Queue ?? registration.Queue;
            if (queue.Length is 0 or > JobLimits.MaxQueueLength)
            {
                errors.Add($"Recurring '{definition.Code}': queue '{queue}' is not valid.");
                continue;
            }

            string payload;
            try
            {
                payload = serializer.Serialize(definition.Payload, definition.PayloadType);
                ScheduleOf(definition.Cron, definition.TimeZone);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Recurring '{definition.Code}': {ex.Message}");
                continue;
            }

            rows.Add(new RecurringDefinitionRow(
                definition.Code, registration.Code, queue, payload, definition.Cron, definition.TimeZone, definition.Options.MisfirePolicy,
                definition.Options.AllowOverlap, definition.Options.DefinitionVersion));
        }

        if (_options.MisfireThreshold <= _options.TickInterval)
        {
            errors.Add($"NinjaQueue:Recurring:MisfireThreshold ({_options.MisfireThreshold}) must be longer than TickInterval ({_options.TickInterval}).");
        }

        return errors.Count > 0 ? throw new JobRegistryException(errors) : rows;
    }
}

/// <summary>
/// Runs the recurring scheduler in the host: validates the schedules at start (an invalid schedule refuses the start), then
/// syncs them (retrying while the database is unreachable) and ticks every <see cref="NinjaQueueRecurringOptions.TickInterval"/>
/// while holding the <c>scheduler.recurring</c> lease.
/// </summary>
internal sealed class RecurringSchedulerService(
    RecurringScheduler scheduler, IServiceScopeFactory scopes, IOptions<NinjaQueueOptions> options, ILogger<RecurringSchedulerService> logger)
    : IHostedService, IAsyncDisposable
{
    /// <summary>The maintenance lease the firing instance holds (§14.6, §15.9).</summary>
    public const string LeaseName = "scheduler.recurring";

    private readonly NinjaQueueRecurringOptions _options = options.Value.Recurring;
    private readonly CancellationTokenSource _stop = new();
    private readonly Guid _token = Guid.NewGuid();
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:recurring";
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        scheduler.Validate();
        if (_options.Enabled)
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
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>().ReleaseLeaseAsync(LeaseName, _token, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or DbException or TimeoutException or InvalidOperationException)
        {
            RecurringLog.Failed(logger, ex, "stop");
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task RunAsync(CancellationToken stop)
    {
        TimeSpan delay = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await scheduler.SyncAsync(stop);
                break;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                RecurringLog.Failed(logger, ex, "sync");
                await DelayAsync(delay, stop);
                delay = TimeSpan.FromMilliseconds(Math.Min(60_000, delay.TotalMilliseconds * 2));
            }
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<IJobMaintenanceStore>().TryAcquireLeaseAsync(LeaseName, _owner, _token, _options.TickInterval * 3, stop))
                {
                    await scheduler.TickAsync(stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            {
                RecurringLog.Failed(logger, ex, "tick");
            }

            await DelayAsync(_options.TickInterval, stop);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}

internal static partial class RecurringLog
{
    [LoggerMessage(EventId = 7200, Level = LogLevel.Information,
        Message = "Recurring schedules synced: {Inserted} inserted, {Updated} updated, {Unchanged} unchanged, {SkippedNewer} kept (newer stored definition), {Disabled} disabled")]
    public static partial void Synced(ILogger logger, int inserted, int updated, int unchanged, int skippedNewer, int disabled);

    [LoggerMessage(EventId = 7201, Level = LogLevel.Information,
        Message = "Recurring tick: {Schedules} schedules, {Enqueued} enqueued, {Skipped} skipped (overlap), {Deduplicated} deduplicated, {Failed} failed")]
    public static partial void Ticked(ILogger logger, int schedules, int enqueued, int skipped, int deduplicated, int failed);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Recurring schedule {Code}: job type {JobType} is not registered on this instance; left for an instance that knows it")]
    public static partial void UnknownJobType(ILogger logger, string code, string jobType);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning, Message = "Recurring schedule {Code}: more missed occurrences than FireAllCap ({Cap}); only the latest were enqueued")]
    public static partial void FireAllTruncated(ILogger logger, string code, int cap);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Error, Message = "Recurring scheduler {Stage} failed")]
    public static partial void Failed(ILogger logger, Exception exception, string stage);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Error, Message = "Recurring schedule {Code} could not fire; it is retried on the next tick")]
    public static partial void ScheduleFailed(ILogger logger, Exception exception, string code);
}