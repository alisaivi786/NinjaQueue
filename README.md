# NinjaQueue

Durable, database-backed background jobs for .NET, on **PostgreSQL** or **SQL Server** through **EF Core**.
No broker, no extra server: jobs live in their own schema in your database (or a dedicated one), and NinjaQueue
installs and upgrades that schema itself.

- **Transactional outbox**: stage a job inside your own transaction; it commits or rolls back with your data.
- **Exactly-once database work**: fenced jobs run their writes in the engine's transaction, finalized atomically.
- **Leases, retries, recovery**: crashed workers are recovered, retries back off, dead letters are kept and can be handled.
- **Recurring jobs**: cron schedules with time zones, misfire policies and overlap control.
- **Keys**: idempotency, unique and concurrency keys, all tenant-scoped.
- **Multi-tenant**: every job carries its tenant; admin queries are scoped to the caller's tenants.
- **Dashboard**: a built-in web UI and HTTP API (`app.MapNinjaQueueDashboard("/jobs")`) with its own users, tenant switcher,
  retry / requeue / cancel and bulk actions, recurring schedules and live workers.
- **Health and metrics**: an ASP.NET Core health check and `System.Diagnostics.Metrics` instruments.

## Try it in 2 minutes

```bash
git clone https://github.com/alisaivi786/NinjaQueue
cd NinjaQueue/samples/NinjaQueue.Demo
docker compose up --build                     # PostgreSQL; add --profile sqlserver for SQL Server on :8081
```

- **http://localhost:8080**: the Demo Shop. Each button creates real jobs: fire-and-forget, an order with an outbox and a
  continuation, a declined card that dead-letters, a delayed reminder, a long import with progress, a 200-job burst.
- **http://localhost:8080/jobs**: the dashboard. Sign in as `admin` / `admin` (you choose a new password), or as the tenant
  users `acme` / `globex` (password `demo-pass-123`) to see what a single tenant sees.

## Packages

| Package | What it is |
|---|---|
| `NinjaQueue.Abstractions` | Contracts your job code references: handlers, `IJobEnqueuer`, `[JobType]`. |
| `NinjaQueue` | The engine: workers, retries, recurring schedules, maintenance. |
| `NinjaQueue.EntityFrameworkCore` | The EF Core store and the schema migrations (provider-neutral). |
| `NinjaQueue.PostgreSql` | PostgreSQL (13+): `UsePostgreSql()`, LISTEN/NOTIFY instant wake-up. |
| `NinjaQueue.SqlServer` | SQL Server (compatibility level 130+): `UseSqlServer()`. |
| `NinjaQueue.Dashboard` | The web dashboard and its HTTP API: `MapNinjaQueueDashboard()`. |

## Quick start

```bash
dotnet add package NinjaQueue.PostgreSql     # or NinjaQueue.SqlServer
```

```csharp
// Program.cs
builder.Services.AddNinjaQueue(builder.Configuration)
    .UsePostgreSql(builder.Configuration.GetConnectionString("Jobs")!)   // or .UseSqlServer(...)
    .AddJobHandlersFromAssembly(typeof(Program).Assembly);
```

```csharp
// A job: a payload record and its handler.
public sealed record SendWelcomeEmail(Guid UserId);

[JobType("Users.SendWelcomeEmail.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, Queue = "email", MaxAttempts = 5)]
public sealed class SendWelcomeEmailHandler(IEmailSender email) : IJobHandler<SendWelcomeEmail>
{
    public async Task<JobResult> HandleAsync(SendWelcomeEmail payload, JobContext context, CancellationToken ct)
    {
        await email.SendWelcomeAsync(payload.UserId, context.OperationKey("send"), ct);
        return JobResult.Success();
    }
}

// Enqueue it.
await jobs.EnqueueAsync(new SendWelcomeEmail(user.Id));
```

On start NinjaQueue creates its schema (`ninjaqueue` by default) and its own migration history table
(`ninjaqueue.__ninjaqueue_migrations`). Your tables and your own EF Core migration history are never touched.

## Commit a job with your own data (transactional outbox)

Map the job tables into your own context and point NinjaQueue at it:

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.AddInterceptors(FencedTransactionGuard.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyNinjaQueue();   // excluded from YOUR migrations: NinjaQueue migrates them itself
}

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddNinjaQueue(builder.Configuration).UsePostgreSql<AppDbContext>();
```

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await jobs.StageAsync(new SendOrderConfirmation(order.Id), cancellationToken: ct);   // same transaction
await tx.CommitAsync(ct);                                                             // both, or neither
```

Applications whose repositories open their own unit of work plug it in through `IJobTransactionLocator<TContext>`
and `IJobTransactionParticipant`.

## Dashboard

```csharp
builder.Services.AddNinjaQueueDashboard();
app.MapNinjaQueueDashboard("/jobs");          // the UI at /jobs, its API at /jobs/api
```

- **Its own sign-in**, independent of your app's authentication. On first start there is one admin,
  `NinjaQueue:Dashboard:Admin:UserName` / `Password` (default `admin` / `admin`), who must choose a new password before
  anything else; after that only the stored hash counts. Passwords are PBKDF2-SHA256 hashes; five failed sign-ins lock an
  account for 15 minutes; a password change ends the user's other sessions.
- **Tenants**: admins see every tenant and can filter by one; other users see only the tenants they are assigned (enforced
  on the server: another tenant's job answers 404). Add users and tenant names in the dashboard, or from code with
  `NinjaQueueDashboardUsers.UpsertTenantAsync` / `EnsureUserAsync`.
- **Built-in HTTP API** (plain minimal APIs, JSON `{ "data": ... }`): `auth/login`, `auth/me`, `auth/change-password`, `jobs`,
  `jobs/{id}`, `jobs/{id}/retry|requeue|cancel`, `jobs/bulk/{operation}`, `jobs/stats`, `jobs/lookups`, `jobs/recurring`,
  `jobs/recurring/{code}/pause|resume|trigger`, `jobs/workers`, `tenants`, `users`. State-changing calls need the
  `X-NinjaQueue-Csrf: 1` header. Every job action needs a reason, recorded in the job's history.
- Sessions are encrypted with ASP.NET Core Data Protection: with more than one instance, share the key ring
  (`AddDataProtection().PersistKeysTo...`) so a sign-in works on every instance.

## Configuration

```json
{
  "NinjaQueue": {
    "Schema": "ninjaqueue",
    "AutoMigrate": true,
    "Worker": { "Enabled": true, "Queues": { "default": 4, "email": 2 } },
    "Retry": { "MaxAttempts": 10, "BaseDelay": "00:00:15", "MaxDelay": "01:00:00" },
    "ListenNotify": { "Enabled": true }
  }
}
```

Where a DBA applies schema changes, set `AutoMigrate` to `false` and hand over the idempotent script from
`INinjaQueueSchemaMigrator.GenerateScriptAsync()`.

## Building and testing

```bash
cd src/NinjaQueue.Dashboard/ui && npm ci && npm run build && cd -   # the dashboard UI (Node.js 20+); also built on first dotnet build
dotnet build NinjaQueue.slnx
dotnet test tests/NinjaQueue.Tests
dotnet test tests/NinjaQueue.IntegrationTests       # needs Docker (PostgreSQL 16 and SQL Server 2022 via Testcontainers)
```

## License

MIT