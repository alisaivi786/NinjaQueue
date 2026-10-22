using NinjaQueue.Admin;

namespace NinjaQueue.Storage;

/// <summary>
/// Admin storage for Release 2 (§14.4, §15.11, §17.8, §17.9). Every method takes the caller's tenant scopes (null = all
/// tenants) and treats a job outside them exactly like a missing one. Every mutation is one short transaction per job and
/// writes its <c>job_events</c> row with the actor and the reason in that transaction.
/// </summary>
public interface IJobAdministrationStore
{
    /// <summary>Cancels one job (§14.4): queued and Awaiting at once (continuations cascade), running cooperatively when
    /// <paramref name="allowRunning"/> (<see cref="CancelResult.CancelAfterCurrentAttempt"/> when <paramref name="isInterruptible"/>
    /// says the job type is not interruptible). <paramref name="bulkOperationId"/> adds a <c>BulkOperationApplied</c> event.</summary>
    Task<AdminCancelResult> CancelJobAsync(
        JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, Guid? actorId, string reason, bool allowRunning, Func<string, bool> isInterruptible,
        Guid? bulkOperationId, CancellationToken cancellationToken);

    /// <summary>Admin Retry or Requeue of one job (§15.11).</summary>
    Task<AdminRunResult> RunAgainAsync(JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, AdminRunRequest request, Guid? bulkOperationId, CancellationToken cancellationToken);

    /// <summary>One job with history (payload unredacted: the service decides whether to return it), or null.</summary>
    Task<JobDetail?> GetJobAsync(JobId jobId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken);

    /// <summary>One keyset page (newest first): <paramref name="afterId"/> is the last database id of the previous page.</summary>
    Task<(IReadOnlyList<JobSummary> Items, long? LastId)> ListJobsAsync(JobQuery query, long? afterId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken);

    /// <summary>Ids matching a bulk filter after <paramref name="afterId"/>, oldest first: (database id, job id).</summary>
    Task<IReadOnlyList<(long Id, JobId JobId)>> FindBulkJobsAsync(
        BulkJobFilter filter, IReadOnlyCollection<Guid>? tenantScopes, long afterId, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a bulk operation in one transaction serialized per tenant: a repeat of (tenant, actor, idempotency key) returns
    /// the stored operation; another active operation of the tenant rejects it; otherwise inserts the operation row and, for a
    /// filter, stages <paramref name="job"/> in the same transaction.
    /// </summary>
    Task<BulkStart> StartBulkOperationAsync(BulkOperationRow operation, NewJob? job, CancellationToken cancellationToken);

    /// <summary>Stores the report and counts of an operation.</summary>
    Task SaveBulkReportAsync(Guid operationId, BulkOperationReport report, string? state, CancellationToken cancellationToken);

    /// <summary>Queue and status counts within the scopes; terminal counts since <paramref name="sinceUtc"/> (§16.8).</summary>
    Task<JobStats> GetStatsAsync(IReadOnlyCollection<Guid>? tenantScopes, DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Pauses an enabled schedule: <c>enabled = false</c> and a non-null <c>paused_by</c> (the actor, or the system scope when
    /// there is none), so the startup sync keeps it paused. Writes <c>RecurringPaused</c> on the schedule's last job, if any.
    /// </summary>
    Task<RecurringAdminOutcome> PauseRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes an admin-paused schedule with <c>next_run_at_utc = <paramref name="nextAfter"/>(cron, time zone, database now)</c>
    /// (missed occurrences are not replayed). Writes <c>RecurringResumed</c> on the schedule's last job, if any.
    /// </summary>
    Task<RecurringAdminOutcome> ResumeRecurringAsync(
        string code, Guid? actorId, string reason, Func<string, string, DateTimeOffset, DateTimeOffset> nextAfter, CancellationToken cancellationToken);

    /// <summary>
    /// Enqueues one occurrence of an enabled schedule now, in one transaction under the schedule's row lock: the job that
    /// <paramref name="build"/> returns for (schedule, database now) goes through the normal enqueue path, so the occurrence's
    /// idempotency key and, without AllowOverlap, the schedule's unique key apply exactly as for a timed occurrence. A held
    /// unique key is <see cref="RecurringAdminOutcome.Conflict"/> and nothing is enqueued. <c>next_run_at_utc</c> is unchanged;
    /// <c>last_job_id</c> / <c>last_run_at_utc</c> record the occurrence, which carries a <c>RecurringTriggered</c> event.
    /// <paramref name="build"/> returns null when this instance does not run the schedule's job type (NotEligible).
    /// </summary>
    Task<RecurringTriggerResult> TriggerRecurringAsync(
        string code, Guid? actorId, string reason, Func<RecurringTriggerSchedule, NewJob?> build, CancellationToken cancellationToken);

    /// <summary>Workers whose heartbeat is newer than <paramref name="livenessWindow"/>.</summary>
    Task<IReadOnlyList<JobWorkerInfo>> GetWorkersAsync(TimeSpan livenessWindow, CancellationToken cancellationToken);

    /// <summary>The operation, or null when missing or outside the caller's scopes.</summary>
    Task<BulkOperationRecord?> GetBulkOperationAsync(Guid operationId, IReadOnlyCollection<Guid>? tenantScopes, CancellationToken cancellationToken);
}

/// <summary>A locked, enabled schedule that an admin triggers now.</summary>
/// <param name="Code">The schedule code.</param>
/// <param name="JobType">The job type code.</param>
/// <param name="Queue">The queue.</param>
/// <param name="Payload">The stored payload (JSON).</param>
/// <param name="AllowOverlap">Whether occurrences may overlap.</param>
/// <param name="DatabaseNow">The database clock: the occurrence time.</param>
public sealed record RecurringTriggerSchedule(string Code, string JobType, string Queue, string? Payload, bool AllowOverlap, DateTimeOffset DatabaseNow);

/// <summary>What an admin Retry/Requeue asks for.</summary>
/// <param name="Mode">Retry or Requeue.</param>
/// <param name="ActorId">Who acts.</param>
/// <param name="Reason">Required reason.</param>
/// <param name="RunAtUtc">Requeue: the start time (null = now).</param>
/// <param name="IncludeContinuations">Restore children cancelled because of this job.</param>
/// <param name="DefaultAttempts">The attempt budget of a job type (registry, or the configured default for unknown types).</param>
public sealed record AdminRunRequest(AdminRunMode Mode, Guid? ActorId, string Reason, DateTimeOffset? RunAtUtc, bool IncludeContinuations, Func<string, int> DefaultAttempts);

/// <summary>A bulk operation row to insert.</summary>
public sealed record BulkOperationRow(
    Guid OperationId, Guid TenantScope, AdminOperationKind Operation, Guid ActorId, string Reason, string IdempotencyKey, string StateJson, int Requested, bool Async);

/// <summary>The result of <see cref="IJobAdministrationStore.StartBulkOperationAsync"/>.</summary>
public sealed record BulkStart(BulkSubmissionKind Kind, BulkOperationRecord? Existing);

/// <summary>A stored bulk operation: the report plus what the bulk job needs to resume.</summary>
public sealed record BulkOperationRecord(BulkOperationReport Report, Guid TenantScope, Guid ActorId, string Reason, string? StateJson);