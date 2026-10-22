using Microsoft.EntityFrameworkCore;

namespace NinjaQueue.Demo;

// The Demo Shop's background jobs: a payload record plus its handler, one pair per job type.
// Each [JobType] declares a stable code, how its side effects are made safe, and whether it belongs to a tenant.

/// <summary>Fire-and-forget: a welcome email after sign-up.</summary>
public sealed record SendWelcomeEmail(string Email);

[JobType("Shop.SendWelcomeEmail.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, Queue = "email", MaxAttempts = 5, DashboardVisible = true)]
public sealed class SendWelcomeEmailHandler(ILogger<SendWelcomeEmailHandler> logger) : IJobHandler<SendWelcomeEmail>
{
    public async Task<JobResult> HandleAsync(SendWelcomeEmail payload, JobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); // pretend to talk to an SMTP server
        DemoLog.EmailSent(logger, "welcome", payload.Email, context.TenantId);
        return JobResult.Success();
    }
}

/// <summary>Outbox: staged in the same transaction as the order, so it exists only if the order does.</summary>
public sealed record SendOrderConfirmation(Guid OrderId, string Email);

[JobType("Shop.SendOrderConfirmation.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, Queue = "email", DashboardVisible = true)]
public sealed class SendOrderConfirmationHandler(ILogger<SendOrderConfirmationHandler> logger) : IJobHandler<SendOrderConfirmation>
{
    public async Task<JobResult> HandleAsync(SendOrderConfirmation payload, JobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        DemoLog.EmailSent(logger, "order confirmation", payload.Email, context.TenantId);
        return JobResult.Success();
    }
}

/// <summary>Retries and dead letters: a declined card fails every attempt until an admin retries it from the dashboard.</summary>
public sealed record ChargePayment(Guid OrderId, decimal Amount, bool SimulateDecline);

[JobType("Shop.ChargePayment.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, Queue = "payments", MaxAttempts = 3, DashboardVisible = true)]
public sealed class ChargePaymentHandler(DemoDbContext db) : IJobHandler<ChargePayment>
{
    public async Task<JobResult> HandleAsync(ChargePayment payload, JobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        // Attempts 1-3 fail, so the job dead-letters; "Retry" in the dashboard runs attempt 4, which succeeds.
        if (payload.SimulateDecline && context.Attempt <= 3)
        {
            throw new InvalidOperationException($"Card declined (simulated, attempt {context.Attempt}).");
        }

        await db.Orders.Where(o => o.Id == payload.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "Paid"), cancellationToken);
        return JobResult.Success();
    }
}

/// <summary>Continuation: waits for the payment job and runs only after it succeeds.</summary>
public sealed record ShipOrder(Guid OrderId);

[JobType("Shop.ShipOrder.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, DashboardVisible = true)]
public sealed class ShipOrderHandler(DemoDbContext db) : IJobHandler<ShipOrder>
{
    public async Task<JobResult> HandleAsync(ShipOrder payload, JobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await db.Orders.Where(o => o.Id == payload.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "Shipped"), cancellationToken);
        return JobResult.Success();
    }
}

/// <summary>Delayed: a reminder that runs at a set time.</summary>
public sealed record SendCartReminder(string Email);

[JobType("Shop.SendCartReminder.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, Queue = "email", DashboardVisible = true)]
public sealed class SendCartReminderHandler(ILogger<SendCartReminderHandler> logger) : IJobHandler<SendCartReminder>
{
    public Task<JobResult> HandleAsync(SendCartReminder payload, JobContext context, CancellationToken cancellationToken)
    {
        DemoLog.EmailSent(logger, "cart reminder", payload.Email, context.TenantId);
        return Task.FromResult(JobResult.Success());
    }
}

/// <summary>Long-running with progress: watch the bar move in the dashboard, or cancel it while it runs.</summary>
public sealed record ImportCatalog(int Products);

[JobType("Shop.ImportCatalog.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, Queue = "imports", Timeout = "00:10:00", DashboardVisible = true)]
public sealed class ImportCatalogHandler : IJobHandler<ImportCatalog>
{
    public async Task<JobResult> HandleAsync(ImportCatalog payload, JobContext context, CancellationToken cancellationToken)
    {
        int total = Math.Clamp(payload.Products, 1, 500);
        for (int i = 1; i <= total; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken); // one product
            if (i % 5 == 0 || i == total)
            {
                await context.ReportProgressAsync(i, total, cancellationToken);
            }
        }

        return JobResult.Success();
    }
}

/// <summary>Recurring: a platform job (no tenant) on a cron schedule. Pause, resume or run it now from the dashboard.</summary>
public sealed record SalesReport;

[JobType("Shop.SalesReport.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, Queue = "reports")]
public sealed class SalesReportHandler(DemoDbContext db, ILogger<SalesReportHandler> logger) : IJobHandler<SalesReport>
{
    public async Task<JobResult> HandleAsync(SalesReport payload, JobContext context, CancellationToken cancellationToken)
    {
        int orders = await db.Orders.CountAsync(cancellationToken);
        DemoLog.Report(logger, orders);
        return JobResult.Success();
    }
}

internal static partial class DemoLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Sent {Kind} email to {Email} (tenant {TenantId})")]
    public static partial void EmailSent(ILogger logger, string kind, string email, Guid? tenantId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sales report: {Orders} orders so far")]
    public static partial void Report(ILogger logger, int orders);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Demo tenants and users are ready. Open /jobs and sign in as admin / admin")]
    public static partial void Seeded(ILogger logger);
}