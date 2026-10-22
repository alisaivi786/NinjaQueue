namespace NinjaQueue;

/// <summary>The <c>NinjaQueue</c> configuration section (§7.7).</summary>
public sealed class NinjaQueueOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "NinjaQueue";

    /// <summary>The database schema of the job tables.</summary>
    public string Schema { get; set; } = JobSchemaName.Default;

    /// <summary>
    /// Applies pending NinjaQueue migrations when the host starts, before any worker runs (default true). Set it to false where
    /// a DBA applies schema changes, and give them the script from <c>INinjaQueueSchemaMigrator.GenerateScriptAsync</c>.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>The largest serialized payload accepted at enqueue, in UTF-8 bytes.</summary>
    public int MaxPayloadBytes { get; set; } = 65_536;

    /// <summary>Worker settings.</summary>
    public NinjaQueueWorkerOptions Worker { get; set; } = new();

    /// <summary>Retry and attempt-accounting settings.</summary>
    public NinjaQueueRetryOptions Retry { get; set; } = new();

    /// <summary>Retention settings.</summary>
    public NinjaQueueRetentionOptions Retention { get; set; } = new();

    /// <summary>
    /// Release 2: cooperative cancel of running jobs (§14.4). Off in Release 1, where only queued jobs can be cancelled and a
    /// cancel request for a running job is refused.
    /// </summary>
    public bool EnableRunningJobCancel { get; set; }

    /// <summary>Degrade the health check when a queue's oldest ready job waits longer than this.</summary>
    public TimeSpan OldestPendingDegradedAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Watchdog settings (§16.3).</summary>
    public NinjaQueueWatchdogOptions Watchdog { get; set; } = new();

    /// <summary>Recurring schedule settings (Release 2, §14.6, §15.8).</summary>
    public NinjaQueueRecurringOptions Recurring { get; set; } = new();

    /// <summary>PostgreSQL LISTEN/NOTIFY wake-up (Release 2, opt-in).</summary>
    public NinjaQueueListenNotifyOptions ListenNotify { get; set; } = new();
}

/// <summary>Worker settings.</summary>
public sealed class NinjaQueueWorkerOptions
{
    /// <summary>Whether this instance runs workers. An instance with workers disabled can still enqueue.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Named queues and their concurrency.</summary>
    public Dictionary<string, int> Queues { get; set; } = new(StringComparer.Ordinal) { ["default"] = 4 };

    /// <summary>How often an idle queue polls.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The lease a claim takes; renewed every third of it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Self-fence when a renewal cannot be confirmed this long before the lease expires (§14.2).</summary>
    public TimeSpan LeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Shutdown stage 1: handlers keep running (§16.2).</summary>
    public TimeSpan ShutdownDrainPeriod { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>Shutdown stage 2: the shutdown token fires (§16.2).</summary>
    public TimeSpan ShutdownCancelPeriod { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Shutdown stage 4: outcomes, leases and the worker row are cleaned up (§16.2).</summary>
    public TimeSpan ShutdownCleanupPeriod { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an ignored cancel is tolerated before the lease is let go (§14.4).</summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A pending job no live worker can run is flagged orphaned after this (§14.3).</summary>
    public TimeSpan UnknownTypeOrphanAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A worker whose heartbeat is older than this is not live.</summary>
    public TimeSpan WorkerLivenessWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often maintenance runs (recovery, orphans, purge, watchdog).</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Retry and attempt-accounting settings (§7.3, §14.1).</summary>
public sealed class NinjaQueueRetryOptions
{
    /// <summary>The first backoff step.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The backoff ceiling.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The default attempt budget.</summary>
    public int MaxAttempts { get; set; } = JobLimits.DefaultMaxAttempts;

    /// <summary>Snoozes allowed before a snooze counts as a failure.</summary>
    public int MaxSnoozes { get; set; } = 50;

    /// <summary>Shutdown releases allowed before a release is charged.</summary>
    public int MaxReleases { get; set; } = 10;

    /// <summary>The absolute age ceiling of a job.</summary>
    public TimeSpan MaxJobAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Failed runs of a job's dead-letter handler before the engine gives up on it (<c>JobDeadLetterHandlerAbandoned</c>).</summary>
    public int MaxDeadLetterHandlerAttempts { get; set; } = 5;
}

/// <summary>Two-tier retention (§14.7).</summary>
public sealed class NinjaQueueRetentionOptions
{
    /// <summary>Operational tier for succeeded jobs.</summary>
    public TimeSpan Succeeded { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Operational tier for cancelled jobs.</summary>
    public TimeSpan Cancelled { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Operational tier for dead-lettered jobs.</summary>
    public TimeSpan DeadLettered { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Audit tier: the slim job row.</summary>
    public TimeSpan AuditRow { get; set; } = TimeSpan.FromDays(400);

    /// <summary>Rows per purge batch.</summary>
    public int PurgeBatchSize { get; set; } = 5000;
}

/// <summary>Fenced-transaction watchdog settings (§16.3).</summary>
public sealed class NinjaQueueWatchdogOptions
{
    /// <summary>Allow fenced job types on SQL Server without the kill permission (reduced guarantee, health Degraded).</summary>
    public bool AllowFencedWithoutWatchdog { get; set; }
}