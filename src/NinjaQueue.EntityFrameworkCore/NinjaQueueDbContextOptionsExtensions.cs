using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>Options for <see cref="NinjaQueueDbContext"/>.</summary>
public static class NinjaQueueDbContextOptionsExtensions
{
    /// <summary>The migration history table, kept in NinjaQueue's own schema so the application's history is never touched.</summary>
    public const string MigrationsHistoryTable = "__ninjaqueue_migrations";

    /// <summary>Sets the schema of the job tables (a lowercase identifier).</summary>
    public static DbContextOptionsBuilder UseNinjaQueueSchema(this DbContextOptionsBuilder optionsBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(new NinjaQueueSchemaOptionsExtension(JobSchemaName.Validate(schema)));
        return optionsBuilder;
    }
}

/// <summary>Carries the schema into <see cref="NinjaQueueDbContext"/> and its migrations. Part of the service-provider key, so
/// each schema gets its own cached model.</summary>
internal sealed class NinjaQueueSchemaOptionsExtension(string schema) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public string Schema { get; } = schema;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(NinjaQueueSchemaOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private string Schema => ((NinjaQueueSchemaOptionsExtension)Extension).Schema;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"NinjaQueueSchema={Schema} ";

        public override int GetServiceProviderHashCode() => StringComparer.Ordinal.GetHashCode(Schema);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info && string.Equals(info.Schema, Schema, StringComparison.Ordinal);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["NinjaQueue:Schema"] = Schema.ToString(CultureInfo.InvariantCulture);
        }
    }
}