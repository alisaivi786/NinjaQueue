namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>The no-op payload. Empty, so seeded rows can carry the literal payload <c>{}</c>.</summary>
public sealed record NoOpPayload;

/// <summary>The §14.8 "no-op handler" used for sustained throughput, lock contention and backlog tests.</summary>
[JobType(Code, SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, Queue = "default", MaxAttempts = 3)]
public sealed class NoOpHandler : IJobHandler<NoOpPayload>
{
    public const string Code = "Perf.NoOp.v1";

    public Task<JobResult> HandleAsync(NoOpPayload payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Success());
}

/// <summary>A no-op job on its own queue, for enqueue → start latency.</summary>
public sealed record LatencyPayload(int Index);

[JobType(Code, SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, Queue = QueueName, MaxAttempts = 3)]
public sealed class LatencyHandler : IJobHandler<LatencyPayload>
{
    public const string Code = "Perf.Latency.v1";
    public const string QueueName = "latency";

    public Task<JobResult> HandleAsync(LatencyPayload payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Success());
}

/// <summary>A fenced job: one step that writes exactly one business row through a nested unit of work.</summary>
public sealed record FencedPayload(int Index);

[JobType(Code, SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, Queue = QueueName, MaxAttempts = 3, StepTimeout = "00:00:30")]
public sealed class FencedHandler(PerfWidgetProvider widgets) : IFencedJobHandler<FencedPayload>
{
    public const string Code = "Perf.Fenced.v1";
    public const string QueueName = "fenced";

    public async Task<Step> HandleStepAsync(FencedPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await widgets.AddAsync($"{context.JobId.Value:N}", cancellationToken);
        return Step.Complete();
    }
}