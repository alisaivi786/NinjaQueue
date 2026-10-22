namespace NinjaQueue.Hosting;

/// <summary>
/// The watchdog's reachability as this instance sees it (§16.3 <c>watchdog_unreachable</c>). A maintenance round counts as OK
/// when this instance either ran the watchdog (it holds <c>maintenance.watchdog</c>, the session views were readable and every
/// overdue step was handled) or confirmed through the database that another instance holds the lease. It counts as failed when
/// the lease or the watchdog itself could not reach the database or its session views.
/// </summary>
internal sealed class WatchdogMonitor(TimeProvider clock)
{
    private readonly DateTimeOffset _createdUtc = clock.GetUtcNow();
    private long _lastOkTicks;
    private long _lastFailureTicks;

    public DateTimeOffset? LastOkUtc => Read(ref _lastOkTicks);

    public DateTimeOffset? LastFailureUtc => Read(ref _lastFailureTicks);

    public void Ok() => Interlocked.Exchange(ref _lastOkTicks, clock.GetUtcNow().UtcTicks);

    public void Failed() => Interlocked.Exchange(ref _lastFailureTicks, clock.GetUtcNow().UtcTicks);

    /// <summary>
    /// Unreachable: the latest round failed and no round has succeeded for longer than <paramref name="window"/> (counted from
    /// this monitor's creation when none ever did). A loop that is merely slow, or has never failed, is not reported.
    /// </summary>
    public bool IsUnreachable(TimeSpan window)
    {
        DateTimeOffset? failed = LastFailureUtc;
        DateTimeOffset? ok = LastOkUtc;
        return failed is not null && (ok is null || failed > ok) && clock.GetUtcNow() - (ok ?? _createdUtc) > window;
    }

    private static DateTimeOffset? Read(ref long ticks)
    {
        long value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}