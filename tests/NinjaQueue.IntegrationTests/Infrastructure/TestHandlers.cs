
namespace NinjaQueue.IntegrationTests.Infrastructure;

public sealed record WidgetPayload(Guid TenantId, string Name);

/// <summary>
/// The G4 handler: two fenced steps, each writing through <see cref="WidgetProvider"/> (a nested unit of work that must
/// join the engine's transaction), driven by <see cref="FaultPlan"/>.
/// </summary>
[JobType("Test.Widgets.Create.v1", SideEffects = JobSideEffects.DatabaseFenced, Tenancy = JobTenancy.TenantOwned, MaxAttempts = 3, StepTimeout = "00:00:20")]
public sealed class CreateWidgetsHandler(WidgetProvider widgets, FaultPlan faults, IServiceProvider services) : IFencedJobHandler<WidgetPayload>
{
    public async Task<Step> HandleStepAsync(WidgetPayload payload, FencedJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.Increment(ref faults.Calls);
        context.EnsureTenant(payload.TenantId);
        string checkpoint = context.Checkpoint ?? "start";

        await widgets.AddAsync($"{payload.Name}-{checkpoint}", cancellationToken);

        if (faults.ThrowPermanent)
        {
            throw new PermanentJobException("Injected permanent failure.");
        }

        if (faults.ThrowAfterWriteAtCheckpoint == checkpoint)
        {
            throw new InvalidOperationException($"Injected failure after the provider write at '{checkpoint}'.");
        }

        if (faults.CommitEngineTransaction)
        {
            // Committing the engine's transaction early would make the step's writes durable before the finalization.
            AppSessions sessions = services.GetRequiredService<AppSessions>();
            await sessions.Current.DbContext.Database.CommitTransactionAsync(cancellationToken);
        }

        if (faults.OpenSecondUnitOfWork)
        {
            // A fresh scope's unit of work is not joined: it opens a second connection, which the guard refuses.
            await using AsyncServiceScope other = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            await other.ServiceProvider.GetRequiredService<WidgetProvider>().AddAsync($"{payload.Name}-escaped", cancellationToken);
        }

        if (faults.DuringStep is { } during)
        {
            await during();
        }

        context.ReportProgress(checkpoint == "start" ? 1 : 2, 2);
        return checkpoint == "start" ? Step.Continue("second") : Step.Complete();
    }
}

public sealed record SystemPingPayload(string Note);

/// <summary>A standard (idempotent) system job.</summary>
[JobType("Test.System.Ping.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, Queue = "default")]
public sealed class SystemPingHandler : IJobHandler<SystemPingPayload>
{
    public Task<JobResult> HandleAsync(SystemPingPayload payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Success());
}

public sealed record ExternalPayload(string To);

/// <summary>An ExternalLedger handler without OnAmbiguous: the registry must refuse it.</summary>
[JobType("Test.External.Send.v1", SideEffects = JobSideEffects.ExternalLedger, Tenancy = JobTenancy.System)]
public sealed class ExternalSendHandler : IJobHandler<ExternalPayload>
{
    public Task<JobResult> HandleAsync(ExternalPayload payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Success());
}