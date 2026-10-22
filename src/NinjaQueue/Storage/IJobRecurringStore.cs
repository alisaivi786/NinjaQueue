namespace NinjaQueue.Storage;

/// <summary>
/// Recurring schedule storage (§14.6, §15.8) over <c>job_recurring</c>. The schedule row is the fence: firing locks it
/// (<c>FOR UPDATE SKIP LOCKED</c> / <c>UPDLOCK, READPAST</c>), enqueues with the occurrence idempotency key and advances
/// <c>next_run_at_utc</c> in one transaction, so any number of schedulers produce one job per occurrence.
/// </summary>
public interface IJobRecurringStore
{
    /// <summary>
    /// Upserts the schedules defined in code (§14.6). A row is written only when the code's definition version is at least the
    /// stored one (rolling deploys never flip-flop). A changed cron or time zone, or a schedule re-added after removal, gets its
    /// next run recomputed from now with <paramref name="nextAfter"/>(definition, database now). An admin pause is kept. When
    /// <paramref name="disableRemoved"/>, stored schedules not in <paramref name="definitions"/> are disabled, never deleted.
    /// </summary>
    Task<RecurringSyncResult> SyncAsync(
        IReadOnlyList<RecurringDefinitionRow> definitions, Func<RecurringDefinitionRow, DateTimeOffset, DateTimeOffset> nextAfter, bool disableRemoved,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fires up to <paramref name="maxSchedules"/> due schedules, each in its own short transaction (§15.8): lock the row, ask
    /// <paramref name="plan"/> what to enqueue, enqueue (reserving <c>recurring:{code}</c> when overlap is not allowed, else
    /// recording one <c>RecurringSkippedOverlap</c> on the blocking job), advance the row and commit. A null plan (job type
    /// unknown to this instance) leaves the row untouched for an instance that knows it.
    /// </summary>
    Task<RecurringTickResult> FireDueAsync(int maxSchedules, Func<DueRecurringSchedule, RecurringFire?> plan, CancellationToken cancellationToken);

    /// <summary>Reads every stored schedule (admin, health and tests).</summary>
    Task<IReadOnlyList<RecurringScheduleState>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>One schedule as defined in code, ready to sync.</summary>
public sealed record RecurringDefinitionRow(
    string Code, string JobType, string Queue, string Payload, string Cron, string TimeZone, MisfirePolicy MisfirePolicy, bool AllowOverlap, int DefinitionVersion);

/// <summary>A locked, due schedule row plus the database clock of its transaction.</summary>
public sealed record DueRecurringSchedule(
    long Id, string Code, string JobType, string Queue, string? Payload, string Cron, string TimeZone, MisfirePolicy MisfirePolicy, bool AllowOverlap,
    DateTimeOffset NextRunAtUtc, DateTimeOffset DatabaseNow);

/// <summary>What to do with a due schedule: the jobs to enqueue (one per occurrence) and the next run.</summary>
public sealed record RecurringFire(IReadOnlyList<RecurringOccurrenceJob> Jobs, DateTimeOffset NextRunAtUtc);

/// <summary>One occurrence and the job that represents it (its idempotency key is <c>recurring:{code}:{occurrenceUtc:O}</c>).</summary>
public sealed record RecurringOccurrenceJob(DateTimeOffset OccurrenceUtc, NewJob Job);

/// <summary>What a sync did.</summary>
public sealed record RecurringSyncResult(int Inserted, int Updated, int Unchanged, int SkippedNewerStored, int Disabled);

/// <summary>What one tick did.</summary>
public sealed record RecurringTickResult(int Schedules, int Enqueued, int SkippedOverlap, int Deduplicated, int Failed, IReadOnlyList<string> Queues)
{
    /// <summary>Nothing was due.</summary>
    public static RecurringTickResult Empty { get; } = new(0, 0, 0, 0, 0, []);

    /// <summary>Why schedules failed (rolled back, retried on the next tick): schedule code and error.</summary>
    public IReadOnlyList<(string Code, Exception Error)> Failures { get; init; } = [];
}

/// <summary>A stored schedule.</summary>
public sealed record RecurringScheduleState(
    string Code, string JobType, string Queue, string Cron, string TimeZone, MisfirePolicy MisfirePolicy, bool AllowOverlap, int DefinitionVersion,
    bool Enabled, Guid? PausedBy, DateTimeOffset NextRunAtUtc, DateTimeOffset? LastRunAtUtc, long? LastJobId);