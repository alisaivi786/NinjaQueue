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
- **Admin API**: list, detail, retry, requeue, cancel and bulk operations, stats, workers.
- **Health and metrics**: an ASP.NET Core health check and `System.Diagnostics.Metrics` instruments.

## Packages

| Package | What it is |
|---|---|
| `NinjaQueue.Abstractions` | Contracts your job code references: handlers, `IJobEnqueuer`, `[JobType]`. |
| `NinjaQueue` | The engine: workers, retries, recurring schedules, maintenance. |
| `NinjaQueue.EntityFrameworkCore` | The EF Core store and the schema migrations (provider-neutral). |
| `NinjaQueue.PostgreSql` | PostgreSQL (13+): `UsePostgreSql()`, LISTEN/NOTIFY instant wake-up. |
| `NinjaQueue.SqlServer` | SQL Server (compatibility level 130+): `UseSqlServer()`. |

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
dotnet build NinjaQueue.slnx
dotnet test tests/NinjaQueue.Tests
dotnet test tests/NinjaQueue.IntegrationTests       # needs Docker (PostgreSQL 16 and SQL Server 2022 via Testcontainers)
```

## License

MIT