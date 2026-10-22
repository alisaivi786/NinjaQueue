using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.IntegrationTests.Phase4;

/// <summary>Observations and hooks shared by the Phase 4 handlers (one per host).</summary>
public sealed class Phase4Probe(TestDatabase database)
{
    public TestDatabase Database { get; } = database;

    /// <summary>Handler invocations per job.</summary>
    public ConcurrentDictionary<Guid, int> Invocations { get; } = new();

    /// <summary>Successful handler returns per job.</summary>
    public ConcurrentDictionary<Guid, int> Successes { get; } = new();

    /// <summary>(attempt, max attempts) seen by handlers while running.</summary>
    public ConcurrentQueue<(Guid Job, int Attempt, int MaxAttempts)> Seen { get; } = new();

    /// <summary>Runs inside a handler (standard or fenced) after its business write, with the job id and checkpoint.</summary>
    public Func<Guid, string?, Task>? During { get; set; }

    /// <summary>Runs inside a race handler's step with the step's own DI scope (its ambient session), after <see cref="During"/>.</summary>
    public Func<IServiceProvider, Guid, Task>? DuringScoped { get; set; }

    /// <summary>Upper bound of the random delay a race handler waits before returning its step.</summary>
    public int RaceMaxDelayMs { get; set; }

    internal void Record(JobContextBase context)
    {
        Invocations.AddOrUpdate(context.JobId.Value, 1, (_, n) => n + 1);
        Seen.Enqueue((context.JobId.Value, context.Attempt, context.MaxAttempts));
    }

    internal void Succeeded(JobId id) => Successes.AddOrUpdate(id.Value, 1, (_, n) => n + 1);
}

public sealed record P4Payload(string Mode, int FailFirstAttempts = 0, int DelayMs = 0);

/// <summary>A standard (idempotent) job whose behaviour the payload chooses.</summary>
[JobType("Test.P4.Standard.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 3, Timeout = "00:00:30")]
public sealed class P4StandardHandler(Phase4Probe probe) : IJobHandler<P4Payload>
{
    public async Task<JobResult> HandleAsync(P4Payload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        if (probe.During is { } during)
        {
            await during(context.JobId.Value, null);
        }

        if (payload.DelayMs > 0)
        {
            await Task.Delay(payload.DelayMs, cancellationToken);
        }

        if (context.Attempt <= payload.FailFirstAttempts)
        {
            throw new InvalidOperationException($"Injected failure on attempt {context.Attempt}.");
        }

        JobResult result = payload.Mode switch
        {
            "succeed" => JobResult.Success(),
            "fail" => throw new InvalidOperationException("Injected failure."),
            "permanent" => throw new PermanentJobException("Injected permanent failure."),
            "snooze" => JobResult.Snooze(TimeSpan.FromSeconds(1)),
            "hang" => await HangAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(payload), payload.Mode, "Unknown mode."),
        };
        if (result.Kind == JobResultKind.Success)
        {
            probe.Succeeded(context.JobId);
        }

        return result;
    }

    private static async Task<JobResult> HangAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return JobResult.Success();
    }
}

public sealed record P4OtherPayload(string Note);

/// <summary>A second standard type: the one a worker does not advertise in the capability-filter tests.</summary>
[JobType("Test.P4.Other.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System)]
public sealed class P4OtherHandler(Phase4Probe probe) : IJobHandler<P4OtherPayload>
{
    public Task<JobResult> HandleAsync(P4OtherPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        probe.Succeeded(context.JobId);
        return Task.FromResult(JobResult.Success());
    }
}

public sealed record P4TimeoutPayload(string Note);

/// <summary>A standard job that ignores time: it is stopped only by its 500 ms timeout.</summary>
[JobType("Test.P4.Timeout.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 2, Timeout = "00:00:00.500")]
public sealed class P4TimeoutHandler(Phase4Probe probe) : IJobHandler<P4TimeoutPayload>
{
    public async Task<JobResult> HandleAsync(P4TimeoutPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return JobResult.Success();
    }
}

public sealed record RacePayload(int Steps);

public sealed record RaceNonInterruptiblePayload(int Steps);

/// <summary>Shared body of the race handlers: each step writes "{job:N}-{checkpoint}" through a provider's nested unit of work.</summary>
internal static class RaceStep
{
    public static async Task<Step> RunAsync(int steps, FencedJobContext context, WidgetProvider widgets, Phase4Probe probe, IServiceProvider services, CancellationToken ct)
    {
        probe.Record(context);
        string checkpoint = context.Checkpoint ?? "start";
        await widgets.AddAsync($"{context.JobId.Value:N}-{checkpoint}", ct);
        if (probe.During is { } during)
        {
            await during(context.JobId.Value, checkpoint);
        }

        if (probe.DuringScoped is { } scoped)
        {
            await scoped(services, context.JobId.Value);
        }

        if (probe.RaceMaxDelayMs > 0)
        {
            await Task.Delay(RandomNumberGenerator.GetInt32(0, probe.RaceMaxDelayMs + 1), ct);
        }

        return steps > 1 && checkpoint == "start" ? Step.Continue("second") : Step.Complete();
    }
}

/// <summary>The G11 fenced handler (interruptible): one step (Complete) or two (Continue then Complete).</summary>
[JobType("Test.P4.Race.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 3, StepTimeout = "00:00:20")]
public sealed class RaceHandler(WidgetProvider widgets, Phase4Probe probe, IServiceProvider services) : IFencedJobHandler<RacePayload>
{
    public Task<Step> HandleStepAsync(RacePayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        return RaceStep.RunAsync(payload.Steps, context, widgets, probe, services, cancellationToken);
    }
}

/// <summary>The G11 non-interruptible fenced handler: a cancel applies only after the attempt (§14.4).</summary>
[JobType("Test.P4.RaceNonInterruptible.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 3, StepTimeout = "00:00:20",
    Interruptible = false)]
public sealed class RaceNonInterruptibleHandler(WidgetProvider widgets, Phase4Probe probe, IServiceProvider services) : IFencedJobHandler<RaceNonInterruptiblePayload>
{
    public Task<Step> HandleStepAsync(RaceNonInterruptiblePayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        return RaceStep.RunAsync(payload.Steps, context, widgets, probe, services, cancellationToken);
    }
}

public sealed record FencedTimeoutPayload(string Note);

/// <summary>A fenced step that writes, then outlives its 1 s step timeout.</summary>
[JobType("Test.P4.FencedTimeout.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 2, StepTimeout = "00:00:01")]
public sealed class FencedTimeoutHandler(WidgetProvider widgets, Phase4Probe probe) : IFencedJobHandler<FencedTimeoutPayload>
{
    public async Task<Step> HandleStepAsync(FencedTimeoutPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        await widgets.AddAsync($"{context.JobId.Value:N}-timeout", cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return Step.Complete();
    }
}

public sealed record CountPayload(int Index);

/// <summary>The concurrency-suite fenced job: exactly one business row per job when it succeeds (§14.8 invariant 4).</summary>
[JobType("Test.P4.FencedCount.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 5, StepTimeout = "00:00:20")]
public sealed class FencedCountHandler(WidgetProvider widgets, Phase4Probe probe) : IFencedJobHandler<CountPayload>
{
    public async Task<Step> HandleStepAsync(CountPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        await widgets.AddAsync($"{context.JobId.Value:N}-count", cancellationToken);

        // About 3 % of first attempts fail after the write: the write must roll back and the retry must write it once.
        if (context.Attempt == 1 && payload.Index % 31 == 7)
        {
            throw new InvalidOperationException("Injected failure after the business write.");
        }

        probe.Succeeded(context.JobId);
        return Step.Complete();
    }
}

public sealed record GuardPayload(string Bypass);

/// <summary>
/// The G14 handler: writes one row through the supported path (a provider's nested unit of work), then tries the bypass the
/// payload names (§17.7 enforcement table).
/// </summary>
[JobType("Test.P4.Guard.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.System, MaxAttempts = 3, StepTimeout = "00:00:20")]
public sealed class GuardHandler(
    WidgetProvider widgets, Phase4Probe probe, IServiceProvider services, AppSessions sessions) : IFencedJobHandler<GuardPayload>
{
    public async Task<Step> HandleStepAsync(GuardPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        probe.Record(context);
        string prefix = $"{context.JobId.Value:N}";
        await widgets.AddAsync($"{prefix}-supported", cancellationToken);

        switch (payload.Bypass)
        {
            case "none":
                // The supported path: a repository on the ambient session, and a second provider call that joins.
                sessions.Current.DbContext.Widgets.Add(new Widget { Name = $"{prefix}-repository" });
                await sessions.Current.DbContext.SaveChangesAsync(cancellationToken);
                break;
            case "new-context-save":
                {
                    await using TestHostDbContext other = TestHostDbContext.Create(probe.Database);
                    other.Widgets.Add(new Widget { Name = $"{prefix}-escaped" });
                    await other.SaveChangesAsync(cancellationToken);
                    break;
                }

            case "new-context-connection":
                {
                    await using TestHostDbContext other = TestHostDbContext.Create(probe.Database);
                    string escaped = $"{prefix}-escaped";
                    await other.Database.ExecuteSqlAsync($"INSERT INTO test_widgets (name) VALUES ({escaped})", cancellationToken);
                    break;
                }

            case "separate-scope-uow":
                {
                    await using AsyncServiceScope scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<WidgetProvider>().AddAsync($"{prefix}-escaped", cancellationToken);
                    break;
                }

            case "begin-transaction":
                await using (await sessions.Current.DbContext.Database.BeginTransactionAsync(cancellationToken))
                {
                    sessions.Current.DbContext.Widgets.Add(new Widget { Name = $"{prefix}-escaped" });
                    await sessions.Current.DbContext.SaveChangesAsync(cancellationToken);
                }

                break;
            case "commit":
                await sessions.Current.DbContext.Database.CommitTransactionAsync(cancellationToken);
                break;
            case "rollback":
                await sessions.Current.DbContext.Database.RollbackTransactionAsync(cancellationToken);
                break;
            case "other-context-guarded":
                {
                    await using OtherGuardedDbContext other = OtherGuardedDbContext.Create(probe.Database, guarded: true);
                    string escaped = $"{prefix}-escaped";
                    await other.Database.ExecuteSqlAsync($"INSERT INTO test_widgets (name) VALUES ({escaped})", cancellationToken);
                    break;
                }

            case "transaction-scope":
                {
                    using System.Transactions.TransactionScope ts = new(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
                    await widgets.AddAsync($"{prefix}-scoped", cancellationToken);
                    ts.Complete();
                    break;
                }

            case "raw-ado":
                {
                    await using DbConnection raw = probe.Database.CreateConnection();
                    await raw.OpenAsync(cancellationToken);
                    await using DbCommand insert = raw.CreateCommand();
                    insert.CommandText = $"INSERT INTO test_widgets (name) VALUES ('{prefix}-escaped')";
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(payload), payload.Bypass, "Unknown bypass.");
        }

        return Step.Complete();
    }
}

/// <summary>Another DbContext type of the host, optionally with the fenced guard registered on it (§17.7 row 4).</summary>
public sealed class OtherGuardedDbContext(DbContextOptions<OtherGuardedDbContext> options) : DbContext(options)
{
    public static OtherGuardedDbContext Create(TestDatabase database, bool guarded)
    {
        ArgumentNullException.ThrowIfNull(database);
        DbContextOptionsBuilder<OtherGuardedDbContext> builder = new();
        if (database.IsPostgres)
        {
            builder.UseNpgsql(database.ConnectionString);
        }
        else
        {
            builder.UseSqlServer(database.ConnectionString);
        }

        if (guarded)
        {
            builder.AddInterceptors(FencedTransactionGuard.Instance);
        }

        return new OtherGuardedDbContext(builder.Options);
    }
}