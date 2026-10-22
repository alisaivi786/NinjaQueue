using Microsoft.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore;

namespace NinjaQueue.Demo;

/// <summary>The shop's own data. It also maps the NinjaQueue tables, so an order and its jobs commit in one transaction.</summary>
public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders", "shop");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.TenantId).HasColumnName("tenant_id");
            b.Property(x => x.Customer).HasColumnName("customer").HasMaxLength(200);
            b.Property(x => x.Total).HasColumnName("total").HasPrecision(12, 2);
            b.Property(x => x.Status).HasColumnName("status").HasMaxLength(50);
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        });

        // The job tables, excluded from this context's own schema creation: NinjaQueue installs them itself.
        modelBuilder.ApplyNinjaQueue();
    }
}

/// <summary>A shop order.</summary>
public sealed class Order
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Customer { get; set; } = "";

    public decimal Total { get; set; }

    public string Status { get; set; } = "Placed";

    public DateTimeOffset CreatedAtUtc { get; set; }
}