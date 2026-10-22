using Microsoft.EntityFrameworkCore;
using NinjaQueue.EntityFrameworkCore;
using NinjaQueue.PostgreSql;
using NinjaQueue.SqlServer;

namespace NinjaQueue.IntegrationTests.Infrastructure;

/// <summary>Wires the job store to the test database's provider, exactly as an application does.</summary>
public static class TestHostWiring
{
    /// <summary><c>UsePostgreSql&lt;TestHostDbContext&gt;()</c> or <c>UseSqlServer&lt;TestHostDbContext&gt;()</c> for <paramref name="db"/>.</summary>
    public static NinjaQueueBuilder UseTestDatabase(this NinjaQueueBuilder builder, TestDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.IsPostgres ? builder.UsePostgreSql<TestHostDbContext>() : builder.UseSqlServer<TestHostDbContext>();
    }
}

/// <summary>A business row the test handlers write, standing in for HCM data (shifts, leave policies, ...).</summary>
public sealed class Widget
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>A host DbContext with no naming convention of its own: the kit's explicit names must carry the mapping.</summary>
public sealed class TestHostDbContext(DbContextOptions<TestHostDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();

    public static TestHostDbContext Create(TestDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        DbContextOptionsBuilder<TestHostDbContext> builder = new();
        if (database.IsPostgres)
        {
            builder.UseNpgsql(database.ConnectionString);
        }
        else
        {
            builder.UseSqlServer(database.ConnectionString);
        }

        return new TestHostDbContext(builder.Options);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.AddInterceptors(FencedTransactionGuard.Instance);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Widget>(b =>
        {
            b.ToTable("test_widgets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        });
        modelBuilder.ApplyNinjaQueue();
    }
}