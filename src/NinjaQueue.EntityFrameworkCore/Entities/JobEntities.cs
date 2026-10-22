namespace NinjaQueue.EntityFrameworkCore.Entities;

// The jobs schema (NinjaQueueSchema.V1 + V2 + V3) as EF Core entities. Column names are derived from the property names
// (snake_case) by NinjaQueueModelBuilderExtensions and set explicitly, so the host's naming convention never changes them.
// The schema-contract test (G13) compares the database EF creates from this model with the migration's, column by column.

/// <summary><c>background_jobs</c>: the canonical record, one row per job, kept until retention.</summary>
internal sealed class JobRecord
{
    public long Id { get; set; }
    public Guid GuidId { get; set; }
    public string JobType { get; set; } = "";
    public string Queue { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid TenantScope { get; set; }
    public JobStatus Status { get; set; }
    public string? Payload { get; set; }
    public string? Checkpoint { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? UniqueKey { get; set; }
    public string? ConcurrencyKey { get; set; }
    public int MaxAttempts { get; set; }
    public int Attempt { get; set; }
    public int SnoozeCount { get; set; }
    public int ReleaseCount { get; set; }
    public DateTimeOffset? CancelRequestedAtUtc { get; set; }
    public Guid? CancelRequestedBy { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? RetainUntilUtc { get; set; }
    public DateTimeOffset RunAtUtc { get; set; }
    public long? ParentJobId { get; set; }
    public ContinuationPolicy ContinuationPolicy { get; set; }
    public JobTerminalReason? TerminalReasonCode { get; set; }
    public long? ReplacesJobId { get; set; }
    public DateTimeOffset? OrphanedAtUtc { get; set; }
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? TraceParent { get; set; }
    public Guid? ActorId { get; set; }
    public string? Source { get; set; }
    public int? ProgressProcessed { get; set; }
    public int? ProgressTotal { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    // NinjaQueueSchema.V2: the dead-letter handler marker.
    public DateTimeOffset? DeadLetterHandledAtUtc { get; set; }
    public int? DeadLetterAttempts { get; set; }
}

/// <summary><c>job_queue</c>: dispatch state for active work only; the row is deleted when the job becomes terminal.</summary>
internal sealed class JobQueueEntry
{
    public long JobId { get; set; }
    public string Queue { get; set; } = "";
    public string JobType { get; set; } = "";
    public DateTimeOffset AvailableAtUtc { get; set; }
    public Guid TenantScope { get; set; }
    public string? ConcurrencyKey { get; set; }
    public short AttemptsLeft { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
}

/// <summary><c>job_attempts</c>: one row per claim.</summary>
internal sealed class JobAttemptRecord
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public int AttemptSeq { get; set; }
    public bool Charged { get; set; }
    public string WorkerId { get; set; } = "";
    public Guid LeaseToken { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public JobAttemptOutcome? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public string? ExceptionType { get; set; }
}

/// <summary><c>job_events</c>: the append-only lifecycle audit.</summary>
internal sealed class JobEventRecord
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string EventCode { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? WorkerId { get; set; }
    public Guid? ActorId { get; set; }
    public string? Reason { get; set; }
    public string? Data { get; set; }
}

/// <summary><c>job_recurring</c>: cron schedules defined in code and synced at startup.</summary>
internal sealed class RecurringJobRecord
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string JobType { get; set; } = "";
    public string Queue { get; set; } = "";
    public string? Payload { get; set; }
    public string Cron { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public MisfirePolicy MisfirePolicy { get; set; }
    public bool AllowOverlap { get; set; }
    public int DefinitionVersion { get; set; }
    public bool Enabled { get; set; }
    public Guid? PausedBy { get; set; }
    public DateTimeOffset NextRunAtUtc { get; set; }
    public DateTimeOffset? LastRunAtUtc { get; set; }
    public long? LastJobId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary><c>job_workers</c>: the live worker registry.</summary>
internal sealed class JobWorkerRecord
{
    public string WorkerId { get; set; } = "";
    public string Host { get; set; } = "";
    public int ProcessId { get; set; }
    public string? AppVersion { get; set; }
    public string Queues { get; set; } = "";
    public string JobTypes { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastHeartbeatAtUtc { get; set; }
}

/// <summary><c>job_leases</c>: named maintenance leadership.</summary>
internal sealed class JobLeaseRecord
{
    public string Name { get; set; } = "";
    public string? Owner { get; set; }
    public Guid? Token { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary><c>job_side_effects</c>: the external operation ledger.</summary>
internal sealed class JobSideEffectRecord
{
    public long JobId { get; set; }
    public string StepKey { get; set; } = "";
    public SideEffectStatus Status { get; set; }
    public Guid OwnerLeaseToken { get; set; }
    public int IssueCount { get; set; }
    public string? ExternalRef { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary><c>job_active_keys</c>: <c>unique_key</c> reservations covering every non-terminal state.</summary>
internal sealed class JobActiveKeyRecord
{
    public Guid TenantScope { get; set; }
    public string UniqueKey { get; set; } = "";
    public long JobId { get; set; }
    public DateTimeOffset ReservedAtUtc { get; set; }
}

/// <summary><c>job_fenced_steps</c>: the watchdog registry of open fenced transactions. Written in autocommit on a
/// control connection, so it deliberately has no foreign key into the job tables.</summary>
internal sealed class JobFencedStepRecord
{
    public long JobId { get; set; }
    public Guid LeaseToken { get; set; }
    public int ServerSessionId { get; set; }
    public DateTimeOffset SessionStartUtc { get; set; }
    public long? TransactionId { get; set; }
    public DateTimeOffset DeadlineUtc { get; set; }
    public string OwnerWorker { get; set; } = "";
}

/// <summary><c>job_admin_operations</c>: bulk admin operations and their outcome reports.</summary>
internal sealed class JobAdminOperationRecord
{
    public long Id { get; set; }
    public Guid GuidId { get; set; }
    public Guid TenantScope { get; set; }
    public AdminOperationKind Operation { get; set; }
    public Guid ActorId { get; set; }
    public string Reason { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string? FilterJson { get; set; }
    public AdminOperationStatus Status { get; set; }
    public int RequestedCount { get; set; }
    public int AppliedCount { get; set; }
    public int NotEligibleCount { get; set; }
    public int ConflictCount { get; set; }
    public int NotFoundCount { get; set; }
    public int FailedCount { get; set; }
    public string? ReportJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}