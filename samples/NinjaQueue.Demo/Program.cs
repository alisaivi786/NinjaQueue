using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue;
using NinjaQueue.Admin;
using NinjaQueue.Dashboard;
using NinjaQueue.Demo;
using NinjaQueue.PostgreSql;
using NinjaQueue.SqlServer;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Database: PostgreSQL or SQL Server (Demo:Database), one connection string. ----------------------------------------
string provider = builder.Configuration["Demo:Database"] ?? "PostgreSql";
bool sqlServer = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
string connectionString = builder.Configuration.GetConnectionString("Demo")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Demo.");

builder.Services.AddDbContextFactory<DemoDbContext>(options =>
{
    if (sqlServer)
    {
        options.UseSqlServer(connectionString);
    }
    else
    {
        options.UseNpgsql(connectionString);
    }
});

// ---- NinjaQueue: the engine, the job types in this assembly, one recurring schedule, and the dashboard. ------------------
NinjaQueueBuilder jobs = builder.Services.AddNinjaQueue(builder.Configuration);
_ = sqlServer ? jobs.UseSqlServer<DemoDbContext>() : jobs.UsePostgreSql<DemoDbContext>();
jobs.AddJobHandlersFromAssembly(typeof(Program).Assembly)
    .AddRecurring("shop.sales-report", "* * * * *", "UTC", new SalesReport()); // every minute, so the demo shows it quickly

builder.Services.AddNinjaQueueDashboard();
builder.Services.AddHostedService<DemoSeed>();

WebApplication app = builder.Build();

await DemoSeed.CreateShopTablesAsync(app.Services, sqlServer, app.Lifetime.ApplicationStopping);

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapNinjaQueueDashboard("/jobs");

// ---- Demo Shop API: each button on the shop page calls one of these. ---------------------------------------------------
RouteGroupBuilder demo = app.MapGroup("/demo");

demo.MapGet("/info", () => new { database = sqlServer ? "SQL Server" : "PostgreSQL", tenants = DemoSeed.Tenants });

demo.MapPost("/signup", async (TenantRequest request, IJobEnqueuer enqueuer, CancellationToken ct) =>
{
    // Fire-and-forget: commits on its own.
    JobId id = await enqueuer.EnqueueAsync(new SendWelcomeEmail(request.Email ?? "new.customer@example.com"),
        new JobOptions { TenantId = request.TenantId, Source = "demo-shop" }, ct);
    return new { jobs = new[] { id } };
});

demo.MapPost("/checkout", async (CheckoutRequest request, DemoDbContext db, IJobEnqueuer enqueuer, CancellationToken ct) =>
{
    // Outbox: the order and its three jobs commit together, or (rollback = true) none of them exist.
    Guid orderId = Guid.NewGuid();
    await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
    db.Orders.Add(new Order
    {
        Id = orderId,
        TenantId = request.TenantId,
        Customer = request.Customer ?? "Demo customer",
        Total = request.Total <= 0 ? 49.90m : request.Total,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync(ct);

    JobOptions options = new() { TenantId = request.TenantId, CorrelationId = orderId.ToString(), Source = "demo-shop" };
    JobId confirmation = await enqueuer.StageAsync(new SendOrderConfirmation(orderId, "buyer@example.com"), options, ct);
    JobId payment = await enqueuer.StageAsync(new ChargePayment(orderId, request.Total, request.SimulateDecline), options, ct);
    JobId shipping = await enqueuer.StageAsync(new ShipOrder(orderId), options with { ContinueAfter = payment }, ct); // waits for the payment

    if (request.Rollback)
    {
        await tx.RollbackAsync(ct);
        return Results.Ok(new { orderId, rolledBack = true, jobs = Array.Empty<JobId>() });
    }

    await tx.CommitAsync(ct);
    return Results.Ok(new { orderId, rolledBack = false, jobs = new[] { confirmation, payment, shipping } });
});

demo.MapPost("/reminder", async (ReminderRequest request, IJobEnqueuer enqueuer, CancellationToken ct) =>
{
    // Delayed: runs at a set time (default: in one minute).
    DateTimeOffset runAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(request.DelaySeconds ?? 60, 5, 86_400));
    JobId id = await enqueuer.EnqueueAsync(new SendCartReminder("shopper@example.com"),
        new JobOptions { TenantId = request.TenantId, RunAt = runAt, Source = "demo-shop" }, ct);
    return new { jobs = new[] { id }, runAt };
});

demo.MapPost("/import", async (ImportRequest request, IJobEnqueuer enqueuer, CancellationToken ct) =>
{
    // Long-running with progress (about 0.5 s per product).
    JobId id = await enqueuer.EnqueueAsync(new ImportCatalog(Math.Clamp(request.Products ?? 60, 1, 500)),
        new JobOptions { TenantId = request.TenantId, Source = "demo-shop" }, ct);
    return new { jobs = new[] { id } };
});

demo.MapPost("/burst", async (BurstRequest request, IJobEnqueuer enqueuer, CancellationToken ct) =>
{
    // Load: many jobs in one round trip.
    int count = Math.Clamp(request.Count ?? 50, 1, 1000);
    List<(SendWelcomeEmail, JobOptions?)> batch = [.. Enumerable.Range(1, count)
        .Select(i => (new SendWelcomeEmail($"customer{i}@example.com"), (JobOptions?)new JobOptions { TenantId = request.TenantId, Source = "demo-shop-burst" }))];
    IReadOnlyList<JobId> ids = await enqueuer.EnqueueManyAsync(batch, ct);
    return new { jobs = ids.Count };
});

demo.MapGet("/orders", async (Guid? tenantId, DemoDbContext db, CancellationToken ct)
    => await db.Orders.AsNoTracking()
        .Where(o => tenantId == null || o.TenantId == tenantId)
        .OrderByDescending(o => o.CreatedAtUtc).Take(15)
        .Select(o => new { o.Id, o.TenantId, o.Customer, o.Total, o.Status, o.CreatedAtUtc })
        .ToListAsync(ct));

await app.RunAsync();

internal sealed record TenantRequest(Guid TenantId, string? Email);

internal sealed record CheckoutRequest(Guid TenantId, string? Customer, decimal Total, bool SimulateDecline, bool Rollback);

internal sealed record ReminderRequest(Guid TenantId, int? DelaySeconds);

internal sealed record ImportRequest(Guid TenantId, int? Products);

internal sealed record BurstRequest(Guid TenantId, int? Count);

/// <summary>Creates the shop's own table and seeds two demo tenants with one dashboard user each.</summary>
internal sealed class DemoSeed(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<DemoSeed> logger) : IHostedService
{
    public static readonly DashboardTenant[] Tenants =
    [
        new(Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001"), "Acme Retail"),
        new(Guid.Parse("b2b2b2b2-0000-4000-8000-000000000002"), "Globex Foods"),
    ];

    /// <summary>The shop's table lives in its own "shop" schema; NinjaQueue's tables come from NinjaQueue's migrations.</summary>
    public static async Task CreateShopTablesAsync(IServiceProvider services, bool sqlServer, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        DemoDbContext db = scope.ServiceProvider.GetRequiredService<DemoDbContext>();
        IRelationalDatabaseCreator creator = db.GetService<IRelationalDatabaseCreator>();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!await creator.ExistsAsync(ct))
                {
                    await creator.CreateAsync(ct); // an empty database; the tables follow
                }

                break;
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct); // the database container is still starting
            }
        }

        string sql = sqlServer
            ? """
              IF SCHEMA_ID('shop') IS NULL EXEC('CREATE SCHEMA shop');
              IF OBJECT_ID('shop.orders') IS NULL CREATE TABLE shop.orders (
                  id uniqueidentifier NOT NULL PRIMARY KEY, tenant_id uniqueidentifier NOT NULL, customer nvarchar(200) NOT NULL,
                  total decimal(12,2) NOT NULL, status nvarchar(50) NOT NULL, created_at_utc datetimeoffset NOT NULL);
              """
            : """
              CREATE SCHEMA IF NOT EXISTS shop;
              CREATE TABLE IF NOT EXISTS shop.orders (
                  id uuid NOT NULL PRIMARY KEY, tenant_id uuid NOT NULL, customer varchar(200) NOT NULL,
                  total numeric(12,2) NOT NULL, status varchar(50) NOT NULL, created_at_utc timestamptz NOT NULL);
              """;
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    // Runs after NinjaQueue's schema initializer (registered first), so the dashboard tables exist.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        NinjaQueueDashboardUsers users = scope.ServiceProvider.GetRequiredService<NinjaQueueDashboardUsers>();
        foreach (DashboardTenant tenant in Tenants)
        {
            await users.UpsertTenantAsync(tenant.TenantId, tenant.Name, cancellationToken);
        }

        // One user per tenant, to show what a tenant user sees. They change the password at first sign-in.
        string password = configuration["Demo:TenantUserPassword"] ?? "demo-pass-123";
        foreach ((string userName, DashboardTenant tenant) in new[] { ("acme", Tenants[0]), ("globex", Tenants[1]) })
        {
            await users.EnsureUserAsync(userName, password, isAdmin: false, [tenant.TenantId], cancellationToken);
        }

        DemoLog.Seeded(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}