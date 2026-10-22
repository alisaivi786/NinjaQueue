using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using NinjaQueue.Execution;
using NinjaQueue.Storage;

namespace NinjaQueue.Observability;

/// <summary>The <c>NinjaQueue</c> meter (§7.6). Observable gauges (queue depth, oldest pending) are fed by the sampler.</summary>
public sealed class JobMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NinjaQueue";

    private readonly Meter _meter;
    private readonly Counter<long> _enqueued;
    private readonly Counter<long> _claimed;
    private readonly Counter<long> _completed;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _leaseLost;
    private readonly UpDownCounter<long> _inFlight;
    private volatile IReadOnlyList<QueueSample> _samples = [];

    /// <summary>Creates the instruments.</summary>
    public JobMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _enqueued = _meter.CreateCounter<long>("jobs.enqueued");
        _claimed = _meter.CreateCounter<long>("jobs.claimed");
        _completed = _meter.CreateCounter<long>("jobs.completed");
        _duration = _meter.CreateHistogram<double>("jobs.duration", "ms");
        _retries = _meter.CreateCounter<long>("jobs.retries");
        _deadLettered = _meter.CreateCounter<long>("jobs.dead_lettered");
        _leaseLost = _meter.CreateCounter<long>("jobs.lease_lost");
        _inFlight = _meter.CreateUpDownCounter<long>("jobs.in_flight");
        _meter.CreateObservableGauge("jobs.queue_depth", () => _samples.Select(s => new Measurement<long>(s.Depth, new KeyValuePair<string, object?>("queue", s.Queue))));
        _meter.CreateObservableGauge("jobs.oldest_pending_age", () => _samples.Select(s => new Measurement<double>(s.OldestPendingAge.TotalSeconds, new KeyValuePair<string, object?>("queue", s.Queue))), "s");
    }

    /// <summary>The latest queue sample (also read by the health check).</summary>
    public IReadOnlyList<QueueSample> Samples => _samples;

    /// <summary>Counts an enqueue.</summary>
    public void Enqueued(string jobType) => _enqueued.Add(1, new KeyValuePair<string, object?>("job_type", jobType));

    /// <summary>Counts a job claimed by this process's workers.</summary>
    public void Claimed(string jobType) => _claimed.Add(1, new KeyValuePair<string, object?>("job_type", jobType));

    /// <summary>Counts a finished execution.</summary>
    public void Completed(string jobType, ExecutionResult outcome, TimeSpan duration)
    {
        KeyValuePair<string, object?>[] tags = [new("job_type", jobType), new("outcome", outcome.ToString())];
        _completed.Add(1, tags);
        _duration.Record(duration.TotalMilliseconds, tags);
    }

    /// <summary>Counts a scheduled retry.</summary>
    public void Retried(string jobType) => _retries.Add(1, new KeyValuePair<string, object?>("job_type", jobType));

    /// <summary>Counts a dead-letter (alert metric).</summary>
    public void DeadLettered(string jobType) => _deadLettered.Add(1, new KeyValuePair<string, object?>("job_type", jobType));

    /// <summary>Counts a lost lease.</summary>
    public void LeaseLost(string jobType) => _leaseLost.Add(1, new KeyValuePair<string, object?>("job_type", jobType));

    /// <summary>Tracks jobs running in this process.</summary>
    public void InFlight(int delta) => _inFlight.Add(delta);

    /// <summary>Replaces the queue sample.</summary>
    public void UpdateSamples(IReadOnlyList<QueueSample> samples) => _samples = samples;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}

/// <summary>One queue's depth and oldest pending age.</summary>
public sealed record QueueSample(string Queue, long Depth, TimeSpan OldestPendingAge);

/// <summary>The <c>NinjaQueue</c> activity source; the execute activity links to the enqueue trace (§7.6).</summary>
public static class JobActivitySource
{
    /// <summary>The source name.</summary>
    public const string Name = "NinjaQueue";

    /// <summary>The source.</summary>
    public static readonly ActivitySource Source = new(Name);

    internal static Activity? StartExecute(ClaimedJob job)
    {
        ActivityContext parent = default;
        bool linked = job.TraceParent is not null && ActivityContext.TryParse(job.TraceParent, null, out parent);
        Activity? activity = Source.StartActivity("ninjaqueue.execute", ActivityKind.Consumer, linked ? parent : default);
        activity?.SetTag("ninjaqueue.job_id", job.JobId.Value);
        activity?.SetTag("ninjaqueue.job_type", job.JobType);
        activity?.SetTag("ninjaqueue.attempt", job.Attempt);
        return activity;
    }
}

/// <summary>Source-generated log messages. Payloads are never logged.</summary>
internal static partial class JobLog
{
    [LoggerMessage(EventId = 7100, Level = LogLevel.Information, Message = "Job {JobType} finished: {Outcome} in {DurationMs} ms")]
    public static partial void Finished(ILogger logger, string jobType, ExecutionResult outcome, long durationMs);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "Job {JobType} handler failed")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string jobType);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning, Message = "Job {JobType} lost its lease; its result is discarded")]
    public static partial void LeaseLost(ILogger logger, string jobType);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning, Message = "Queue {Queue} claim failed; retrying in {DelayMs} ms")]
    public static partial void ClaimFailed(ILogger logger, Exception exception, string queue, long delayMs);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Error, Message = "Maintenance task {Task} failed")]
    public static partial void MaintenanceFailed(ILogger logger, Exception exception, string task);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Warning, Message = "Lease renewal for job {JobId} could not be confirmed; self-fencing")]
    public static partial void SelfFenced(ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Information, Message = "Maintenance {Task}: {Count} rows")]
    public static partial void MaintenanceDone(ILogger logger, string task, int count);
}