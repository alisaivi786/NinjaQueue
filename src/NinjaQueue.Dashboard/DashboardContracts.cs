using NinjaQueue.Admin;
using NinjaQueue.Storage;

namespace NinjaQueue.Dashboard;

// The dashboard HTTP API contracts. Every success is { "data": ... }; every error is { "error": { code, message, hint } }.
// Enum values are sent as their names. Payloads never appear in lists; a job's payload appears in its detail only for
// DashboardVisible job types, masked by the engine's redactor.

/// <summary>A success envelope.</summary>
public sealed record DashboardResponse<T>(T Data);

/// <summary>An error envelope.</summary>
public sealed record DashboardErrorResponse(DashboardError Error);

/// <summary>An error: a stable code, a message and an optional hint for the user.</summary>
public sealed record DashboardError(string Code, string Message, string? Hint = null);

/// <summary>One job in a list.</summary>
public sealed record JobSummaryDto(
    Guid Id, string JobType, string Queue, Guid? TenantId, string Status, string? TerminalReason, int Attempt, int MaxAttempts,
    DateTimeOffset CreatedAtUtc, DateTimeOffset RunAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc,
    string? LastErrorCode, string? LastError, Guid? ParentJobId, string? UniqueKey, DateTimeOffset? CancelRequestedAtUtc,
    int? ProgressProcessed, int? ProgressTotal, string? CorrelationId, Guid? ActorId, string? Source)
{
    internal static JobSummaryDto From(JobSummary job) => new(
        job.JobId.Value, job.JobType, job.Queue, job.TenantId, job.Status.ToString(), job.TerminalReason?.ToString(), job.Attempt, job.MaxAttempts,
        job.CreatedAtUtc, job.RunAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.LastErrorCode, job.LastError, job.ParentJobId?.Value,
        job.UniqueKey, job.CancelRequestedAtUtc, job.ProgressProcessed, job.ProgressTotal, job.CorrelationId, job.ActorId, job.Source);
}

/// <summary>One page of jobs.</summary>
public sealed record JobPageDto(IReadOnlyList<JobSummaryDto> Items, string? NextCursor);

/// <summary>One attempt.</summary>
public sealed record JobAttemptDto(
    int AttemptSeq, bool Charged, string WorkerId, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, long? DurationMs, string? Outcome,
    string? ErrorCode, string? Error);

/// <summary>One lifecycle event.</summary>
public sealed record JobEventDto(string EventCode, DateTimeOffset OccurredAtUtc, string? WorkerId, Guid? ActorId, string? Reason, string? Data);

/// <summary>One external-ledger step.</summary>
public sealed record JobSideEffectDto(string StepKey, string Status, int IssueCount, string? ExternalRef, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc);

/// <summary>A job with its history.</summary>
public sealed record JobDetailDto(
    JobSummaryDto Job, string? Payload, IReadOnlyList<JobAttemptDto> Attempts, IReadOnlyList<JobEventDto> Events, IReadOnlyList<JobSideEffectDto> SideEffects)
{
    internal static JobDetailDto From(JobDetail detail) => new(
        JobSummaryDto.From(detail.Job),
        detail.Payload,
        [.. detail.Attempts.Select(a => new JobAttemptDto(a.AttemptSeq, a.Charged, a.WorkerId, a.StartedAtUtc, a.FinishedAtUtc, a.DurationMs, a.Outcome?.ToString(), a.ErrorCode, a.Error))],
        [.. detail.Events.Select(e => new JobEventDto(e.EventCode, e.OccurredAtUtc, e.WorkerId, e.ActorId, e.Reason, e.Data))],
        [.. detail.SideEffects.Select(s => new JobSideEffectDto(s.StepKey, s.Status.ToString(), s.IssueCount, s.ExternalRef, s.StartedAtUtc, s.CompletedAtUtc))]);
}

/// <summary>A single-job action: the reason is required and recorded in the job's events.</summary>
public sealed record JobActionRequest(string? Reason, bool IncludeContinuations = true);

/// <summary>A requeue: now, or at <see cref="RunAtUtc"/>.</summary>
public sealed record JobRequeueRequest(string? Reason, DateTimeOffset? RunAtUtc = null, bool IncludeContinuations = true);

/// <summary>A cancel's result.</summary>
public sealed record JobCancelDto(Guid Id, string Result);

/// <summary>A retry's or requeue's result.</summary>
public sealed record JobRunDto(Guid Id, string Outcome, string? Status, int RestoredContinuations, int ContinuationConflicts);

/// <summary>A bulk selector by filter.</summary>
public sealed record BulkFilterDto(string? Status, string? JobType, Guid? TenantId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? Queue = null);

/// <summary>A bulk request: exactly one of <see cref="JobIds"/> (1-500) or <see cref="Filter"/>.</summary>
public sealed record BulkRequestDto(
    IReadOnlyList<Guid>? JobIds, BulkFilterDto? Filter, string? Reason, string? IdempotencyKey, DateTimeOffset? RunAtUtc = null, bool IncludeContinuations = true);

/// <summary>One job's outcome in a bulk operation.</summary>
public sealed record BulkItemDto(Guid JobId, string Outcome, string? Status, Guid? HolderJobId, string? Error);

/// <summary>A bulk operation's report.</summary>
public sealed record BulkReportDto(
    Guid OperationId, string Operation, string Status, bool Replayed, int Requested, int Applied, int NotEligible, int Conflict, int NotFound,
    int Failed, IReadOnlyList<BulkItemDto> Items, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc)
{
    internal static BulkReportDto From(BulkOperationReport report, bool replayed = false) => new(
        report.OperationId, report.Operation.ToString(), report.Status.ToString(), replayed, report.Requested, report.Applied, report.NotEligible,
        report.Conflict, report.NotFound, report.Failed,
        [.. report.Items.Select(i => new BulkItemDto(i.JobId.Value, i.Outcome.ToString(), i.Status?.ToString(), i.HolderJobId?.Value, i.Error))],
        report.CreatedAtUtc, report.CompletedAtUtc);
}

/// <summary>One queue's live figures.</summary>
public sealed record QueueStatsDto(string Queue, long Depth, double OldestPendingAgeSeconds, long Enqueued, long Scheduled, long Awaiting, long Processing);

/// <summary>Jobs per status: live for active statuses, finished within the window for the rest.</summary>
public sealed record StatusCountsDto(long Enqueued, long Scheduled, long Awaiting, long Processing, long Succeeded, long Cancelled, long DeadLettered);

/// <summary>The overview figures.</summary>
public sealed record StatsDto(
    IReadOnlyList<QueueStatsDto> Queues, long DeadLetteredCount, string Scope, Guid? TenantId, StatusCountsDto Totals, DateTimeOffset SinceUtc,
    double ThroughputPerHour, double? FailureRate);

/// <summary>A recurring schedule.</summary>
public sealed record RecurringScheduleDto(
    string Code, string JobType, string Queue, string Cron, string TimeZone, string MisfirePolicy, bool AllowOverlap, bool Enabled, bool Paused,
    Guid? PausedBy, DateTimeOffset NextRunAtUtc, DateTimeOffset? LastRunAtUtc)
{
    internal static RecurringScheduleDto From(RecurringScheduleState s) => new(
        s.Code, s.JobType, s.Queue, s.Cron, s.TimeZone, s.MisfirePolicy.ToString(), s.AllowOverlap, s.Enabled, !s.Enabled && s.PausedBy is not null,
        s.PausedBy, s.NextRunAtUtc, s.LastRunAtUtc);
}

/// <summary>A schedule action: the reason is required.</summary>
public sealed record RecurringActionRequest(string? Reason);

/// <summary>A "run now" result.</summary>
public sealed record RecurringTriggerDto(Guid JobId, RecurringScheduleDto Schedule);

/// <summary>A registered job type.</summary>
public sealed record JobTypeDto(string Code, string Queue, string Tenancy, string SideEffects, int MaxAttempts, bool DashboardVisible);

/// <summary>Filter options.</summary>
public sealed record LookupsDto(IReadOnlyList<JobTypeDto> JobTypes, IReadOnlyList<string> Queues, IReadOnlyList<string> Statuses);

/// <summary>A live worker.</summary>
public sealed record WorkerDto(string WorkerId, string Host, int ProcessId, string? AppVersion, string Queues, DateTimeOffset StartedAtUtc, DateTimeOffset LastHeartbeatAtUtc);

/// <summary>A login.</summary>
public sealed record LoginRequest(string? UserName, string? Password);

/// <summary>A password change.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>The signed-in user.</summary>
public sealed record MeDto(Guid Id, string UserName, bool IsAdmin, bool MustChangePassword, IReadOnlyList<TenantDto> Tenants, string Title);

/// <summary>A tenant for the switcher.</summary>
public sealed record TenantDto(Guid TenantId, string Name);

/// <summary>A dashboard user (never the hash).</summary>
public sealed record UserDto(Guid Id, string UserName, bool IsAdmin, bool MustChangePassword, bool LockedOut, IReadOnlyList<Guid> TenantIds, DateTimeOffset CreatedAtUtc)
{
    internal static UserDto From(DashboardUser u) => new(
        u.Id, u.UserName, u.IsAdmin, u.MustChangePassword, u.LockedUntilUtc > DateTimeOffset.UtcNow, u.TenantIds, u.CreatedAtUtc);
}

/// <summary>A new user: they must change the password at first login.</summary>
public sealed record CreateUserRequest(string? UserName, string? Password, bool IsAdmin, IReadOnlyList<Guid>? TenantIds);

/// <summary>A user change; a password resets the user to "must change" and signs out their sessions.</summary>
public sealed record UpdateUserRequest(bool IsAdmin, IReadOnlyList<Guid>? TenantIds, string? Password = null);

/// <summary>A tenant name.</summary>
public sealed record UpsertTenantRequest(Guid? TenantId, string? Name);