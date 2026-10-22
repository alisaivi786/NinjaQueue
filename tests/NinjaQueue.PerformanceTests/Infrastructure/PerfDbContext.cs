using Microsoft.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.PerformanceTests.Infrastructure;

/// <summary>The business row a fenced job writes (exactly one per job).</summary>
public sealed class PerfWidget
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>A host DbContext mapping the jobs tables and one business table, with the fenced guard registered.</summary>
public sealed class PerfDbContext(DbContextOptions<PerfDbContext> options) : DbContext(options)
{
    public DbSet<PerfWidget> Widgets => Set<PerfWidget>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PerfWidget>(b =>
        {
            b.ToTable("perf_widgets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        });
        modelBuilder.ApplyNinjaQueue();
    }
}

/// <summary>A repository-style provider over the scoped context: inside a fenced step it writes in the engine's transaction.</summary>
public sealed class PerfWidgetProvider(PerfDbContext dbContext)
{
    public async Task AddAsync(string name, CancellationToken ct)
    {
        dbContext.Widgets.Add(new PerfWidget { Name = name });
        await dbContext.SaveChangesAsync(ct);
    }
}

/// <summary>Wires the job store to the performance database's provider, exactly as an application does.</summary>
public static class PerfWiring
{
    public static NinjaQueueBuilder UsePerfDatabase(this NinjaQueueBuilder builder, PerfDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.IsPostgres
            ? NinjaQueue.PostgreSql.NinjaQueuePostgreSqlExtensions.UsePostgreSql<PerfDbContext>(builder)
            : NinjaQueue.SqlServer.NinjaQueueSqlServerExtensions.UseSqlServer<PerfDbContext>(builder);
    }
}