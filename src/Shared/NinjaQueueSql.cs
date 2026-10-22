namespace NinjaQueue;

// Compiled into NinjaQueue.EntityFrameworkCore (linked source), where the EF model and the hand-written migrations
// use the exact same CHECK and filtered-index SQL. Every fragment is valid on both SQL Server and PostgreSQL:
// unquoted lowercase identifiers, no booleans, no provider functions. Value lists come from the pinned persisted enums.
internal static class NinjaQueueSql
{
    // background_jobs
    internal static readonly string StatusCheck = $"status IN ({Values<JobStatus>()})";
    internal static readonly string MaxAttemptsCheck = $"max_attempts BETWEEN 1 AND {JobLimits.MaxPersistedAttempts}";
    internal const string AttemptCheck = "attempt >= 0 AND attempt <= max_attempts";
    internal const string CountersCheck = "snooze_count >= 0 AND release_count >= 0";
    internal static readonly string ContinuationPolicyCheck = $"continuation_policy IN ({Values<ContinuationPolicy>()})";
    internal static readonly string TerminalReasonCheck = $"terminal_reason_code IS NULL OR terminal_reason_code IN ({Values<JobTerminalReason>()})";
    internal const string TenantScopeCheck =
        "(tenant_id IS NULL AND tenant_scope = '00000000-0000-0000-0000-000000000000') OR (tenant_id IS NOT NULL AND tenant_scope = tenant_id)";
    internal const string IdempotencyFilter = "idempotency_key IS NOT NULL";
    internal static readonly string AwaitingFilter = $"status = {(short)JobStatus.Awaiting}";
    internal static readonly string DeadLetterPendingFilter = $"status = {(short)JobStatus.DeadLettered} AND dead_letter_handled_at_utc IS NULL";
    internal const string HasParentFilter = "parent_job_id IS NOT NULL";

    // job_queue
    internal const string AttemptsLeftCheck = "attempts_left >= 0";
    internal const string ClaimableHasAttemptsCheck = "lease_token IS NOT NULL OR attempts_left >= 1";
    internal const string LeaseCompleteCheck =
        "(lease_token IS NULL AND lease_owner IS NULL AND lease_expires_at_utc IS NULL) OR (lease_token IS NOT NULL AND lease_owner IS NOT NULL AND lease_expires_at_utc IS NOT NULL)";
    internal const string UnleasedFilter = "lease_token IS NULL";
    internal const string LeasedFilter = "lease_token IS NOT NULL";
    internal const string RunningConcurrencyKeyFilter = "concurrency_key IS NOT NULL AND lease_token IS NOT NULL";

    // job_attempts
    internal const string AttemptSeqCheck = "attempt_seq >= 1";
    internal static readonly string OutcomeCheck = $"outcome IS NULL OR outcome IN ({Values<JobAttemptOutcome>()})";

    // job_recurring, job_side_effects, job_admin_operations
    internal static readonly string MisfirePolicyCheck = $"misfire_policy IN ({Values<MisfirePolicy>()})";
    internal static readonly string SideEffectStatusCheck = $"status IN ({Values<SideEffectStatus>()})";
    internal const string IssueCountCheck = "issue_count >= 1";
    internal static readonly string AdminOperationKindCheck = $"operation IN ({Values<AdminOperationKind>()})";
    internal static readonly string AdminOperationStatusCheck = $"status IN ({Values<AdminOperationStatus>()})";

    private static string Values<TEnum>()
        where TEnum : struct, Enum
        => string.Join(", ", Enum.GetValues<TEnum>().Select(v => Convert.ToInt16(v, System.Globalization.CultureInfo.InvariantCulture)).Order());
}