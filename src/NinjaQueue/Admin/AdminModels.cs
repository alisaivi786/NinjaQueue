using NinjaQueue.Storage;

namespace NinjaQueue.Admin;

// ASP.NET-agnostic admin DTOs (§8.3, §16.8, §17.8, §17.9). An HTTP layer maps them 1:1; the results carry everything it
// needs to choose a status code (NotFound → 404, NotEligible/Conflict → 409, validation → 400, Queued → 202).

/// <summary>One job in a list. Never contains the payload.</summary>
public sealed record JobSummary(
    JobId JobId,
    string JobType,
    string Queue,
    Guid? TenantId,
    JobStatus Status,
    JobTerminalReason? TerminalReason,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset RunAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastErrorCode,
    string? LastError,
    JobId? ParentJobId,
    string? UniqueKey,
    string? ConcurrencyKey,
    DateTimeOffset? CancelRequestedAtUtc,
    int? ProgressProcessed,
    int? ProgressTotal,
    string? CorrelationId,
    Guid? ActorId,
    string? Source);

/// <summary>A job with its history. <see cref="Payload"/> is null unless the type is <c>DashboardVisible</c> and the caller
/// asked for it (the host passes that only for callers with the payload permission); sensitive fields are masked.</summary>
public sealed record JobDetail(
    JobSummary Job,
    string? Payload,
    IReadOnlyList<JobAttemptInfo> Attempts,
    IReadOnlyList<JobEventInfo> Events,
    IReadOnlyList<JobSideEffectInfo> SideEffects);

/// <summary>One execution attempt.</summary>
public sealed record JobAttemptInfo(
    int AttemptSeq, bool Charged, string WorkerId, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, long? DurationMs,
    JobAttemptOutcome? Outcome, string? ErrorCode, string? Error);

/// <summary>One lifecycle event.</summary>
public sealed record JobEventInfo(string EventCode, DateTimeOffset OccurredAtUtc, string? WorkerId, Guid? ActorId, string? Reason, string? Data);

/// <summary>One external-ledger step.</summary>
public sealed record JobSideEffectInfo(
    string StepKey, SideEffectStatus Status, int IssueCount, string? ExternalRef, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc);

/// <summary>A job search (keyset-paged, newest first). Every filter is optional; tenant scoping is applied on the server.</summary>
public sealed record JobQuery
{
    /// <summary>Only these statuses.</summary>
    public IReadOnlyList<JobStatus>? Statuses { get; init; }

    /// <summary>Only this job code.</summary>
    public string? JobType { get; init; }

    /// <summary>Only this queue.</summary>
    public string? Queue { get; init; }

    /// <summary>Only this tenant (must be within the caller's scopes, otherwise the page is empty).</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Created at or after.</summary>
    public DateTimeOffset? CreatedFromUtc { get; init; }

    /// <summary>Created before.</summary>
    public DateTimeOffset? CreatedToUtc { get; init; }

    /// <summary>Only continuations of this job.</summary>
    public JobId? ParentJobId { get; init; }

    /// <summary>The <see cref="JobPage.NextCursor"/> of the previous page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page size, 1–<see cref="MaxPageSize"/> (default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>The largest page.</summary>
    public const int MaxPageSize = 200;
}

/// <summary>One page of jobs.</summary>
public sealed record JobPage(IReadOnlyList<JobSummary> Items, string? NextCursor);

/// <summary>The outcome of a single-job Retry or Requeue.</summary>
public enum AdminRunOutcome
{
    /// <summary>The job is Enqueued (or Scheduled) again.</summary>
    Applied = 1,

    /// <summary>No such job in the caller's scopes (404).</summary>
    NotFound = 2,

    /// <summary>The job's state does not allow it (409 with <see cref="AdminRunResult.Status"/>).</summary>
    NotEligible = 3,

    /// <summary>Another active job holds the unique key (409 with <see cref="AdminRunResult.HolderJobId"/>).</summary>
    Conflict = 4,

    /// <summary>The persisted attempt ceiling (1000) would be exceeded (409).</summary>
    AttemptLimitReached = 5,
}

/// <summary>The result of <see cref="IJobAdminService.RetryAsync"/> / <see cref="IJobAdminService.RequeueAsync"/>.</summary>
public sealed record AdminRunResult(
    AdminRunOutcome Outcome, JobStatus? Status = null, JobId? HolderJobId = null, int RestoredContinuations = 0, int ContinuationConflicts = 0,
    Guid? TenantScope = null);

/// <summary>The outcome of an admin pause, resume or trigger of a recurring schedule.</summary>
public enum RecurringAdminOutcome
{
    /// <summary>Paused, resumed or triggered.</summary>
    Applied = 1,

    /// <summary>No such schedule, or the caller is tenant-scoped (schedules are platform-level).</summary>
    NotFound = 2,

    /// <summary>
    /// Already paused / not paused, or disabled because it was removed from code. Trigger: the schedule is paused or disabled,
    /// or this instance does not run its job type.
    /// </summary>
    NotEligible = 3,

    /// <summary>Trigger: an occurrence is still active and the schedule does not allow overlap (nothing was enqueued).</summary>
    Conflict = 4,
}

/// <summary>The result of <see cref="IJobAdminService.TriggerRecurringAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="JobId">Applied: the occurrence enqueued now.</param>
/// <param name="ActiveJobId">Conflict: the still-active occurrence that holds the schedule's overlap key.</param>
public sealed record RecurringTriggerResult(RecurringAdminOutcome Outcome, JobId? JobId = null, JobId? ActiveJobId = null);

/// <summary>The store's result of a single-job cancel: the outcome and the job's tenant scope (null when not found).</summary>
public sealed record AdminCancelResult(CancelResult Result, Guid? TenantScope);

/// <summary>Live counts of one queue (§16.8): non-terminal work only, from the hot queue table.</summary>
public sealed record QueueStats(string Queue, int Enqueued, int Scheduled, int Processing, double OldestEnqueuedAgeSeconds);

/// <summary>Stats for the admin dashboard (§16.8), within the caller's tenant scopes.</summary>
/// <param name="Queues">Per queue: ready, scheduled and running jobs, and the oldest ready job's age.</param>
/// <param name="Awaiting">Continuations waiting for their parent.</param>
/// <param name="Succeeded">Jobs that succeeded since <paramref name="SinceUtc"/>.</param>
/// <param name="Cancelled">Jobs cancelled since <paramref name="SinceUtc"/>.</param>
/// <param name="DeadLettered">Jobs dead-lettered since <paramref name="SinceUtc"/>.</param>
/// <param name="SinceUtc">The start of the terminal-count window.</param>
public sealed record JobStats(IReadOnlyList<QueueStats> Queues, int Awaiting, int Succeeded, int Cancelled, int DeadLettered, DateTimeOffset SinceUtc);

/// <summary>A live worker instance (§5.6). Infrastructure data: the host decides who may see it.</summary>
public sealed record JobWorkerInfo(
    string WorkerId, string Host, int ProcessId, string? AppVersion, IReadOnlyList<string> Queues, IReadOnlyList<string> JobTypes,
    DateTimeOffset StartedAtUtc, DateTimeOffset LastHeartbeatAtUtc);

/// <summary>Selects jobs for a bulk operation by filter (§17.9).</summary>
public sealed record BulkJobFilter
{
    /// <summary>Only this status.</summary>
    public JobStatus? Status { get; init; }

    /// <summary>Only this job code.</summary>
    public string? JobType { get; init; }

    /// <summary>Only this queue (the same filter as <see cref="JobQuery.Queue"/>).</summary>
    public string? Queue { get; init; }

    /// <summary>Only this tenant.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Created at or after.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Created before.</summary>
    public DateTimeOffset? ToUtc { get; init; }
}

/// <summary>A bulk Retry, Requeue or Cancel (§17.9). Exactly one of <see cref="JobIds"/> (1–500) or <see cref="Filter"/>.</summary>
public sealed record BulkRequest
{
    /// <summary>The operation.</summary>
    public required AdminOperationKind Operation { get; init; }

    /// <summary>Explicit ids (1–<see cref="MaxJobIds"/>), processed synchronously.</summary>
    public IReadOnlyList<JobId>? JobIds { get; init; }

    /// <summary>A filter, processed asynchronously by the system job <c>NinjaQueue.Admin.BulkOperation.v1</c>.</summary>
    public BulkJobFilter? Filter { get; init; }

    /// <summary>Required reason, written to every event.</summary>
    public required string Reason { get; init; }

    /// <summary>Required; a repeat with the same key (per tenant and actor) returns the stored report.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Requeue only: the start time (default now).</summary>
    public DateTimeOffset? RunAtUtc { get; init; }

    /// <summary>Retry/Requeue: also restore continuations cancelled because of the parent (default true).</summary>
    public bool IncludeContinuations { get; init; } = true;

    /// <summary>The cap of an explicit id list.</summary>
    public const int MaxJobIds = 500;
}

/// <summary>Per-job outcome in a bulk report.</summary>
public enum BulkItemOutcome
{
    /// <summary>Applied.</summary>
    Applied = 1,

    /// <summary>Wrong state.</summary>
    NotEligible = 2,

    /// <summary>The unique key is held by another job.</summary>
    Conflict = 3,

    /// <summary>Unknown, or outside the caller's scopes.</summary>
    NotFound = 4,

    /// <summary>An unexpected error.</summary>
    Failed = 5,
}

/// <summary>One job in a bulk report.</summary>
public sealed record BulkItem(JobId JobId, BulkItemOutcome Outcome, JobStatus? Status = null, JobId? HolderJobId = null, string? Error = null);

/// <summary>The stored report of a bulk operation.</summary>
public sealed record BulkOperationReport(
    Guid OperationId,
    AdminOperationKind Operation,
    AdminOperationStatus Status,
    int Requested,
    int Applied,
    int NotEligible,
    int Conflict,
    int NotFound,
    int Failed,
    IReadOnlyList<BulkItem> Items,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>How a bulk request was handled.</summary>
public enum BulkSubmissionKind
{
    /// <summary>An id list was applied synchronously (200 with the report).</summary>
    Completed = 1,

    /// <summary>A filter was accepted and runs as a job (202 with the operation id).</summary>
    Queued = 2,

    /// <summary>A repeat of an earlier request with the same idempotency key: its stored report, nothing applied again (200).</summary>
    Replayed = 3,

    /// <summary>Another bulk operation is active for the tenant (409).</summary>
    RejectedActiveOperation = 4,
}

/// <summary>The result of <see cref="IJobAdminService.BulkAsync"/>.</summary>
public sealed record BulkSubmission(BulkSubmissionKind Kind, Guid? OperationId, BulkOperationReport? Report);

/// <summary>A request the admin API must reject with 400; <see cref="Code"/> is a stable machine-readable code.</summary>
public sealed class JobAdminValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public JobAdminValidationException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Creates the exception.</summary>
    public JobAdminValidationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public JobAdminValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public JobAdminValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The validation code (see <see cref="JobAdminErrorCodes"/>).</summary>
    public string Code { get; } = JobAdminErrorCodes.Invalid;
}

/// <summary>Stable validation codes of the admin API.</summary>
public static class JobAdminErrorCodes
{
    public const string Invalid = nameof(Invalid);
    public const string ReasonRequired = nameof(ReasonRequired);
    public const string ActorRequired = nameof(ActorRequired);
    public const string IdempotencyKeyRequired = nameof(IdempotencyKeyRequired);
    public const string BulkSelectorRequired = nameof(BulkSelectorRequired);
    public const string BulkJobIdsLimitExceeded = nameof(BulkJobIdsLimitExceeded);
    public const string InvalidCursor = nameof(InvalidCursor);
    public const string InvalidPageSize = nameof(InvalidPageSize);
    public const string TooManyTenantScopes = nameof(TooManyTenantScopes);
}

/// <summary>
/// Receives every successful admin mutation after it committed, so the host can write its own audit log (HCM audit,
/// §14.7, §16.8). Optional; register it in DI. Bulk operations produce one entry per operation.
/// </summary>
public interface IJobAuditSink
{
    /// <summary>Records one admin action.</summary>
    Task RecordAsync(JobAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>One admin action for the host's audit log.</summary>
/// <param name="Action">Cancel, Retry, Requeue, BulkRetry, BulkRequeue or BulkCancel.</param>
/// <param name="JobId">The job (null for a bulk operation).</param>
/// <param name="OperationId">The bulk operation (null for a single job).</param>
/// <param name="TenantScope">The tenant scope the action applied to.</param>
/// <param name="ActorId">Who acted.</param>
/// <param name="Reason">The required reason.</param>
/// <param name="Outcome">The result, for example <c>Applied</c>, <c>CancelRequested</c>.</param>
public sealed record JobAuditEntry(string Action, JobId? JobId, Guid? OperationId, Guid? TenantScope, Guid? ActorId, string Reason, string Outcome);

/// <summary>Masks payload fields before the admin API returns a payload (§17.8). The default masks <see cref="SensitiveAttribute"/> properties.</summary>
public interface IJobPayloadRedactor
{
    /// <summary>Returns <paramref name="payloadJson"/> with sensitive fields masked.</summary>
    string Redact(string jobType, Type payloadType, string payloadJson);
}