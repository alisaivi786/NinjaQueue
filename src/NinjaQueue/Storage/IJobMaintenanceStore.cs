using NinjaQueue.Observability;

namespace NinjaQueue.Storage;

/// <summary>
/// Maintenance and admin storage (§7.4, §14.3, §14.4, §14.6, §14.7, §15.9, §16.3). Every write is conditional on its own
/// row state, so running any of these twice at once (two leaders) costs extra queries, never duplicates or lost jobs.
/// </summary>
public interface IJobMaintenanceStore
{
    /// <summary>Recovers up to <paramref name="batchSize"/> expired leases: Cancelled when a cancel is pending, DeadLettered
    /// when no attempt remains, otherwise rescheduled after <paramref name="backoff"/>(attempt). Rows held by an open fenced
    /// transaction are skipped. Returns the number recovered.</summary>
    Task<int> RecoverExpiredLeasesAsync(int batchSize, Func<int, TimeSpan> backoff, CancellationToken cancellationToken);

    /// <summary>Upserts this worker's heartbeat and advertised job types.</summary>
    Task HeartbeatAsync(WorkerRegistration worker, CancellationToken cancellationToken);

    /// <summary>Removes this worker's row (shutdown) and rows of workers silent for longer than <paramref name="staleAfter"/>.</summary>
    Task RemoveWorkerAsync(string workerId, TimeSpan staleAfter, CancellationToken cancellationToken);

    /// <summary>Acquires or renews a named maintenance lease; true while this owner holds it.</summary>
    Task<bool> TryAcquireLeaseAsync(string name, string owner, Guid token, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Releases a named lease this owner holds.</summary>
    Task ReleaseLeaseAsync(string name, Guid token, CancellationToken cancellationToken);

    /// <summary>Flags pending jobs whose type no live worker advertises (and clears the flag once one does). Returns jobs flagged.</summary>
    Task<int> DetectOrphansAsync(TimeSpan orphanAfter, TimeSpan livenessWindow, CancellationToken cancellationToken);

    /// <summary>Runs one round of the two-tier purge. Returns rows affected.</summary>
    Task<int> PurgeAsync(NinjaQueueRetentionOptions retention, CancellationToken cancellationToken);

    /// <summary>Kills fenced-step sessions past their deadline plus <paramref name="grace"/>, after confirming identity, and returns
    /// the confirmed kills. Every call first reads the session views, so it throws when the watchdog cannot see sessions (permission
    /// or connectivity) even if nothing is overdue.</summary>
    Task<int> KillOverdueFencedStepsAsync(TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>Depth and oldest ready age per queue.</summary>
    Task<IReadOnlyList<QueueSample>> SampleQueuesAsync(CancellationToken cancellationToken);

    /// <summary>The server facts the startup and health checks need.</summary>
    Task<ServerCheck> CheckServerAsync(CancellationToken cancellationToken);
}

/// <summary>Admin operations (§14.4, §15.11).</summary>
public interface IJobAdminStore
{
    /// <summary>
    /// Cancels a job. A queued job is cancelled at once. A running job gets a cooperative cancel request when
    /// <paramref name="allowRunning"/> is true (Release 2); otherwise <see cref="CancelResult.RunningNotSupported"/>.
    /// A job outside <paramref name="tenantScopes"/> (null = all tenants) is reported as not found.
    /// </summary>
    Task<CancelResult> CancelAsync(JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, Guid? actorId, string reason, bool allowRunning, CancellationToken cancellationToken);
}

/// <summary>A worker's advertised identity (§5.6, §14.3).</summary>
public sealed record WorkerRegistration(string WorkerId, string Host, int ProcessId, string? AppVersion, IReadOnlyList<string> Queues, IReadOnlyList<string> JobTypes);

/// <summary>Server facts for startup and health checks (§15.2, §16.3, §17.6).</summary>
public sealed record ServerCheck(bool IsPostgres, int VersionOrCompatibilityLevel, bool ReadCommittedSnapshot, bool CanKillSessions);

/// <summary>The result of an admin cancel.</summary>
public enum CancelResult
{
    /// <summary>The queued job was cancelled.</summary>
    Cancelled = 1,

    /// <summary>A running job was asked to cancel; it stops cooperatively (or after its attempt if not interruptible).</summary>
    CancelRequested = 2,

    /// <summary>The job is already terminal (<c>JobCancelTooLate</c> was recorded).</summary>
    AlreadyCompleted = 3,

    /// <summary>A cancel was already requested.</summary>
    AlreadyRequested = 4,

    /// <summary>No such job in the caller's tenant scopes.</summary>
    NotFound = 5,

    /// <summary>The job is running and running-job cancel is not enabled (Release 1).</summary>
    RunningNotSupported = 6,

    /// <summary>A running job of a non-interruptible type was asked to cancel: the current attempt finishes, and the cancel
    /// prevents any further attempt (§14.4 step 6).</summary>
    CancelAfterCurrentAttempt = 7,
}