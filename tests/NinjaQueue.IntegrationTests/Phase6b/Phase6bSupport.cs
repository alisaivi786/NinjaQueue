using System.Globalization;
using NinjaQueue.Recurring;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase6b;

/// <summary>Shared helpers for the Phase 6b suites (recurring schedules and the LISTEN/NOTIFY wake-up).</summary>
internal static class Phase6bSupport
{
    public const string Probe = "Test.Probe.v1";

    /// <summary>A host over <paramref name="db"/> with the probe handler and the given schedules; the scheduler loop is off
    /// unless <paramref name="configure"/> turns it on, so the tests drive sync and ticks themselves.</summary>
    public static WorkerTestHost Host(TestDatabase db, Action<NinjaQueueBuilder>? schedules = null, Action<NinjaQueueOptions>? configure = null)
        => WorkerTestHost.Create(db, b =>
        {
            b.AddJobHandler<ProbeHandler>();
            schedules?.Invoke(b);
        }, o =>
        {
            o.Recurring.Enabled = false;
            configure?.Invoke(o);
        });

    public static RecurringScheduler Scheduler(this WorkerTestHost host) => host.Get<RecurringScheduler>();

    public static Task<RecurringSyncResult> SyncAsync(this WorkerTestHost host) => host.Scheduler().SyncAsync(CancellationToken.None);

    public static Task<RecurringTickResult> TickAsync(this WorkerTestHost host) => host.Scheduler().TickAsync(CancellationToken.None);

    public static async Task<RecurringScheduleState> ScheduleAsync(this WorkerTestHost host, string code)
        => (await host.InScopeAsync(sp => sp.GetRequiredService<IJobRecurringStore>().ListAsync(CancellationToken.None))).Single(s => s.Code == code);

    /// <summary>The database clock.</summary>
    public static async Task<DateTimeOffset> DbNowAsync(this TestDatabase db)
        => (await db.QueryAsync($"SELECT {db.Now}")).Single()[0] switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            var other => throw new InvalidCastException(other?.GetType().Name),
        };

    /// <summary>A timestamp literal for this provider.</summary>
    public static string Ts(this TestDatabase db, DateTimeOffset value)
    {
        string text = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture);
        return db.IsPostgres ? $"'{text}'::timestamptz" : $"CAST('{text}' AS datetimeoffset)";
    }

    public static Task<int> SetNextRunAsync(this TestDatabase db, string code, DateTimeOffset value)
        => db.ExecuteAsync($"UPDATE ninjaqueue.job_recurring SET next_run_at_utc = {db.Ts(value)} WHERE code = '{code}'");

    /// <summary>Makes the schedule due one second ago.</summary>
    public static Task<int> MakeDueAsync(this TestDatabase db, string code)
        => db.ExecuteAsync(db.IsPostgres
            ? $"UPDATE ninjaqueue.job_recurring SET next_run_at_utc = now() - interval '1 second' WHERE code = '{code}'"
            : $"UPDATE ninjaqueue.job_recurring SET next_run_at_utc = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE code = '{code}'");

    /// <summary>Idempotency keys of the jobs enqueued by <paramref name="code"/>, sorted.</summary>
    public static async Task<List<string>> OccurrenceKeysAsync(this TestDatabase db, string code)
        => [.. (await db.QueryAsync($"SELECT idempotency_key FROM ninjaqueue.background_jobs WHERE idempotency_key LIKE 'recurring:{code}:%' ORDER BY idempotency_key"))
            .Select(r => (string)r[0]!)];

    public static Task<int> EventCountAsync(this TestDatabase db, string code)
        => db.ScalarAsync<int>($"SELECT count(*) FROM ninjaqueue.job_events WHERE event_code = '{code}'");

    public static string Key(string code, DateTimeOffset occurrence) => RecurringScheduler.IdempotencyKeyFor(code, occurrence);

    public static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    /// <summary>The p-th percentile (nearest rank).</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        double[] sorted = [.. values.Order()];
        int rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}