using Microsoft.EntityFrameworkCore.Storage;

namespace NinjaQueue.IntegrationTests.Infrastructure;

// Scriptable handlers for the Phase 5, Phase 6a and G3 suites. Each test registers the handler(s) it needs plus the plan
// singleton, then scripts the behaviour per payload name through the plan.

public sealed record ProbePayload(string Name);

/// <summary>What <see cref="ProbeHandler"/> does on each call (null = succeed at once).</summary>
public sealed class ProbePlan
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

    public Func<ProbePayload, JobContext, CancellationToken, Task<JobResult>>? Behaviour { get; set; }

    public int Calls;

    /// <summary>Completes when a handler call for <paramref name="name"/> has started.</summary>
    public Task Started(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal void MarkStarted(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>A standard (idempotent) system job driven by <see cref="ProbePlan"/>.</summary>
[JobType("Test.Probe.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 3, Timeout = "00:01:00")]
public sealed class ProbeHandler(ProbePlan plan) : IJobHandler<ProbePayload>
{
    public Task<JobResult> HandleAsync(ProbePayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref plan.Calls);
        plan.MarkStarted(payload.Name);
        return plan.Behaviour?.Invoke(payload, context, cancellationToken) ?? Task.FromResult(JobResult.Success());
    }
}

public sealed record RollingPayload(string Name);

/// <summary>A job type only the "new" build of a rolling deploy registers.</summary>
[JobType("Test.Rolling.NewType.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System)]
public sealed class RollingNewTypeHandler(ProbePlan plan) : IJobHandler<RollingPayload>
{
    public Task<JobResult> HandleAsync(RollingPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref plan.Calls);
        plan.MarkStarted(payload.Name);
        return Task.FromResult(JobResult.Success());
    }
}

public sealed record FencedProbePayload(string Name);

/// <summary>What <see cref="FencedProbeHandler"/> does in each step (null = write one widget and complete).</summary>
public sealed class FencedProbePlan
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

    public Func<FencedProbePayload, FencedJobContext, FencedStepServices, CancellationToken, Task<Step>>? Step { get; set; }

    public int Calls;

    public Task Started(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal void MarkStarted(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}

/// <summary>What a fenced probe step can reach: the joined widget provider and the engine's open connection.</summary>
public sealed class FencedStepServices(WidgetProvider widgets, AppSessions sessions)
{
    public WidgetProvider Widgets { get; } = widgets;

    /// <summary>The fenced step's connection (the engine's transaction).</summary>
    public DbConnection Connection => sessions.Current.Connection;

    /// <summary>The fenced step's transaction.</summary>
    public DbTransaction Transaction => sessions.Current.Transaction!.GetDbTransaction();

    /// <summary>Runs a scalar on the fenced step's own connection and transaction.</summary>
    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}

/// <summary>A database-fenced system job with a short step timeout, driven by <see cref="FencedProbePlan"/>.</summary>
[JobType("Test.Fenced.Probe.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 3, StepTimeout = "00:00:02")]
public sealed class FencedProbeHandler(FencedProbePlan plan, WidgetProvider widgets, AppSessions sessions) : IFencedJobHandler<FencedProbePayload>
{
    public async Task<Step> HandleStepAsync(FencedProbePayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref plan.Calls);
        plan.MarkStarted(payload.Name);
        if (plan.Step is { } step)
        {
            return await step(payload, context, new FencedStepServices(widgets, sessions), cancellationToken);
        }

        await widgets.AddAsync(payload.Name, cancellationToken);
        return Step.Complete();
    }
}

public sealed record LongFencedPayload(string Name);

/// <summary>Like <see cref="FencedProbeHandler"/> with an 8 s step timeout (shutdown and outage tests); same plan.</summary>
[JobType("Test.Fenced.Long.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 3, StepTimeout = "00:00:08")]
public sealed class LongFencedProbeHandler(FencedProbePlan plan, WidgetProvider widgets, AppSessions sessions) : IFencedJobHandler<LongFencedPayload>
{
    public async Task<Step> HandleStepAsync(LongFencedPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Interlocked.Increment(ref plan.Calls);
        plan.MarkStarted(payload.Name);
        if (plan.Step is { } step)
        {
            return await step(new FencedProbePayload(payload.Name), context, new FencedStepServices(widgets, sessions), cancellationToken);
        }

        await widgets.AddAsync(payload.Name, cancellationToken);
        return Step.Complete();
    }
}