namespace NinjaQueue;

/// <summary>Recurring schedule settings (§14.6, §15.8), the <c>NinjaQueue:Recurring</c> section.</summary>
public sealed class NinjaQueueRecurringOptions
{
    /// <summary>
    /// Whether this instance syncs its schedules at startup and runs the scheduler loop. Firing happens only on the instance
    /// holding the <c>scheduler.recurring</c> lease, but correctness never depends on it (the schedule row is the fence).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the scheduler looks for due schedules (§14.6: every 15 s).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// An occurrence later than this when the scheduler reaches it counts as missed, so the misfire policy applies. Must be
    /// longer than <see cref="TickInterval"/>, otherwise a normal tick delay would count as a misfire.
    /// </summary>
    public TimeSpan MisfireThreshold { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most occurrences <see cref="MisfirePolicy.FireAll"/> enqueues in one catch-up (§14.6: 100).</summary>
    public int FireAllCap { get; set; } = 100;

    /// <summary>The most due schedules one tick processes, each in its own short transaction (§15.8: 20).</summary>
    public int MaxSchedulesPerTick { get; set; } = 20;

    /// <summary>
    /// Disable (never delete) stored schedules this instance does not define (§14.6). Applies only on an instance that
    /// defines at least one schedule, so a process that registers no schedules never disables the others. An admin pause
    /// (<c>paused_by</c> set) is never overridden.
    /// </summary>
    public bool DisableRemovedSchedules { get; set; } = true;
}

/// <summary>PostgreSQL LISTEN/NOTIFY wake-up (Release 2), the <c>NinjaQueue:ListenNotify</c> section.</summary>
public sealed class NinjaQueueListenNotifyOptions
{
    /// <summary>
    /// Opt-in. On PostgreSQL, enqueue sends <c>NOTIFY</c> (inside the enqueue transaction, so it is delivered only on commit)
    /// and each worker instance listens on one dedicated connection. Polling always continues as the fallback. Ignored on
    /// SQL Server, which keeps polling plus the in-process signal.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>An idle listener checks its connection this often (a dead socket is found within this interval).</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The first reconnect delay after the listener connection fails; doubled up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest reconnect delay.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
}