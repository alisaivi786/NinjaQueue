using Microsoft.EntityFrameworkCore;

namespace NinjaQueue.EntityFrameworkCore;

/// <summary>
/// The PostgreSQL wake-up channel: one per job schema, the queue name as payload. SQL text only (no driver types), so the store
/// can NOTIFY inside its own transactions; the LISTEN side lives in <c>NinjaQueue.PostgreSql</c>.
/// </summary>
internal static class PostgresWakeup
{
    // PostgreSQL identifiers (and so channel names) are at most 63 bytes.
    private const int MaxChannelLength = 63;

    public static string Channel(string schema)
    {
        string channel = "ninjaqueue_" + JobSchemaName.Validate(schema);
        return channel.Length > MaxChannelLength ? channel[..MaxChannelLength] : channel;
    }

    /// <summary>NOTIFY on the channel with the queue as payload; PostgreSQL folds duplicates within one transaction.</summary>
    public const string NotifySql = "SELECT pg_notify(@channel, @queue)";

    public static bool IsPostgres(DbContext context) => context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}