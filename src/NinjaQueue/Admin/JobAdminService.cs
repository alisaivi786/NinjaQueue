using System.Text.Json;
using Microsoft.Extensions.Options;
using NinjaQueue.Enqueue;
using NinjaQueue.Registry;
using NinjaQueue.Serialization;
using NinjaQueue.Storage;

namespace NinjaQueue.Admin;

/// <summary>Returns the tenant scopes the current caller may act on; null means every tenant (super admin or system).</summary>
public interface IJobTenantAccessor
{
    /// <summary>The caller's allowed tenant scopes, or null for all.</summary>
    IReadOnlyCollection<Guid>? AllowedTenantScopes { get; }
}

/// <summary>
/// Admin operations on jobs (§14.4, §15.11, §16.8, §17.8, §17.9), ASP.NET-agnostic. Tenant scoping is enforced here, on the
/// server, through <see cref="IJobTenantAccessor"/>: a job outside the caller's scopes behaves exactly like a missing one.
/// Every mutation requires a reason, writes <c>job_events</c> with the actor in its own transaction, and then calls the
/// host's <see cref="IJobAuditSink"/>. Authorization policies (Read / Write / Bulk / ViewPayload) belong to the HTTP layer.
/// </summary>
public interface IJobAdminService
{
    /// <summary>Cancels a job (§14.4): queued and Awaiting at once (its continuations cascade), running cooperatively when
    /// <c>EnableRunningJobCancel</c> is on.</summary>
    Task<CancelResult> CancelAsync(JobId jobId, Guid? actorId, string reason, CancellationToken cancellationToken = default);

    /// <summary>One more attempt now for a dead-lettered job (§15.11).</summary>
    Task<AdminRunResult> RetryAsync(JobId jobId, Guid? actorId, string reason, bool includeContinuations = true, CancellationToken cancellationToken = default);

    /// <summary>A fresh attempt budget for a dead-lettered or cancelled job, now or at <paramref name="runAtUtc"/> (§15.11).</summary>
    Task<AdminRunResult> RequeueAsync(
        JobId jobId, Guid? actorId, string reason, DateTimeOffset? runAtUtc = null, bool includeContinuations = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// A job with attempts, events and side effects, or null (missing or out of scope). The payload is returned only when
    /// <paramref name="includePayload"/> (pass it only for callers allowed to see payloads) and the job type is
    /// <c>DashboardVisible</c>, and then with sensitive fields masked by <see cref="IJobPayloadRedactor"/>.
    /// </summary>
    Task<JobDetail?> GetAsync(JobId jobId, bool includePayload = false, CancellationToken cancellationToken = default);

    /// <summary>Searches jobs, newest first, keyset-paged.</summary>
    Task<JobPage> ListAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>A bulk Retry, Requeue or Cancel with the §17.9 contract.</summary>
    Task<BulkSubmission> BulkAsync(BulkRequest request, Guid actorId, CancellationToken cancellationToken = default);

    /// <summary>A bulk operation's report, or null (missing or out of scope).</summary>
    Task<BulkOperationReport?> GetBulkOperationAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dashboard stats within the caller's scopes (§16.8): per queue ready / scheduled / running and the oldest ready age
    /// (from the hot queue table), Awaiting continuations, and terminal counts since <paramref name="sinceUtc"/> (default: 24 h).
    /// </summary>
    Task<JobStats> GetStatsAsync(DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default);

    /// <summary>Live worker instances (heartbeat within <c>WorkerLivenessWindow</c>). Not tenant data: the host decides who sees it.</summary>
    Task<IReadOnlyList<JobWorkerInfo>> GetWorkersAsync(CancellationToken cancellationToken = default);

    /// <summary>Recurring schedules (platform-level: empty for a tenant-scoped caller).</summary>
    Task<IReadOnlyList<RecurringScheduleState>> ListRecurringAsync(CancellationToken cancellationToken = default);

    /// <summary>Pauses a schedule (all-tenants callers only); the pause survives restarts and syncs.</summary>
    Task<RecurringAdminOutcome> PauseRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Resumes an admin-paused schedule from now (missed occurrences are not replayed; all-tenants callers only).</summary>
    Task<RecurringAdminOutcome> ResumeRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues one occurrence of an enabled schedule now (all-tenants callers only; actor and reason required). The schedule's
    /// idempotency and overlap rules apply: with AllowOverlap = false and an occurrence still active, the result is
    /// <see cref="RecurringAdminOutcome.Conflict"/> and nothing is enqueued. The schedule's next run is unchanged. Audited.
    /// </summary>
    Task<RecurringTriggerResult> TriggerRecurringAsync(string code, Guid actorId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>The default admin service.</summary>
internal sealed class JobAdminService(
    IJobAdministrationStore store,
    JobHandlerRegistry registry,
    JobPayloadSerializer serializer,
    IJobSignal signal,
    IOptions<NinjaQueueOptions> options,
    IJobTenantAccessor? tenants = null,
    IJobAuditSink? audit = null,
    IJobPayloadRedactor? redactor = null,
    IJobRecurringStore? recurring = null) : IJobAdminService
{
    private const int MaxTenantScopes = 1000;
    private const int BulkChunk = 50;
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    public async Task<CancelResult> CancelAsync(JobId jobId, Guid? actorId, string reason, CancellationToken cancellationToken = default)
    {
        string why = Reason(reason);
        AdminCancelResult cancel = await store.CancelJobAsync(jobId, Scopes(), actorId, why, options.Value.EnableRunningJobCancel, IsInterruptible, null, cancellationToken);
        if (cancel.Result is CancelResult.Cancelled or CancelResult.CancelRequested or CancelResult.CancelAfterCurrentAttempt)
        {
            await AuditAsync(new JobAuditEntry("Cancel", jobId, null, cancel.TenantScope, actorId, why, cancel.Result.ToString()), cancellationToken);
        }

        return cancel.Result;
    }

    public Task<AdminRunResult> RetryAsync(JobId jobId, Guid? actorId, string reason, bool includeContinuations = true, CancellationToken cancellationToken = default)
        => RunAgainAsync(jobId, new AdminRunRequest(AdminRunMode.Retry, actorId, Reason(reason), null, includeContinuations, DefaultAttempts), cancellationToken);

    public Task<AdminRunResult> RequeueAsync(
        JobId jobId, Guid? actorId, string reason, DateTimeOffset? runAtUtc = null, bool includeContinuations = true, CancellationToken cancellationToken = default)
        => RunAgainAsync(jobId, new AdminRunRequest(AdminRunMode.Requeue, actorId, Reason(reason), runAtUtc?.ToUniversalTime(), includeContinuations, DefaultAttempts),
            cancellationToken);

    public async Task<JobDetail?> GetAsync(JobId jobId, bool includePayload = false, CancellationToken cancellationToken = default)
    {
        JobDetail? detail = await store.GetJobAsync(jobId, Scopes(), cancellationToken);
        if (detail is null)
        {
            return null;
        }

        // Server-side redaction (§17.8): the payload leaves only for a DashboardVisible type and a caller allowed to see it.
        string? payload = null;
        if (includePayload && detail.Payload is { } raw && registry.TryGetByCode(detail.Job.JobType, out JobTypeRegistration? type) && type.DashboardVisible)
        {
            payload = (redactor ?? DefaultJobPayloadRedactor.Instance).Redact(type.Code, type.PayloadType, raw);
        }

        return detail with { Payload = payload };
    }

    public async Task<JobPage> ListAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > JobQuery.MaxPageSize)
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.InvalidPageSize, $"PageSize must be between 1 and {JobQuery.MaxPageSize}.");
        }

        IReadOnlyCollection<Guid>? scopes = Scopes();
        if (query.TenantId is { } tenant && scopes is not null && !scopes.Contains(tenant))
        {
            return new JobPage([], null);
        }

        (IReadOnlyList<JobSummary> items, long? lastId) = await store.ListJobsAsync(query, ParseCursor(query.Cursor), scopes, cancellationToken);
        return new JobPage(items, lastId is { } id ? FormatCursor(id) : null);
    }

    public async Task<BulkSubmission> BulkAsync(BulkRequest request, Guid actorId, CancellationToken cancellationToken = default)
    {
        BulkPlan plan = Validate(request, actorId);
        IReadOnlyCollection<Guid>? scopes = Scopes();
        Guid operationId = Guid.NewGuid();
        Guid tenantScope = OperationScope(scopes, request.Filter?.TenantId);
        BulkState state = new(scopes?.ToArray(), request.Filter, request.RunAtUtc?.ToUniversalTime(), request.IncludeContinuations, 0);
        bool isAsync = request.Filter is not null;
        BulkOperationRow row = new(operationId, tenantScope, request.Operation, actorId, plan.Reason, plan.IdempotencyKey,
            JsonSerializer.Serialize(state, StateJson), plan.JobIds?.Count ?? 0, isAsync);
        NewJob? job = isAsync ? BulkJob(operationId, tenantScope) : null;

        BulkStart start = await store.StartBulkOperationAsync(row, job, cancellationToken);
        switch (start.Kind)
        {
            case BulkSubmissionKind.Replayed:
                return new BulkSubmission(BulkSubmissionKind.Replayed, start.Existing!.Report.OperationId, start.Existing.Report);
            case BulkSubmissionKind.RejectedActiveOperation:
                return new BulkSubmission(BulkSubmissionKind.RejectedActiveOperation, null, null);
            case BulkSubmissionKind.Queued:
                signal.Notify(job!.Queue);
                await AuditAsync(new JobAuditEntry(request.Operation.ToString(), null, operationId, tenantScope, actorId, plan.Reason, "Queued"), cancellationToken);
                return new BulkSubmission(BulkSubmissionKind.Queued, operationId, (await store.GetBulkOperationAsync(operationId, null, cancellationToken))?.Report);
        }

        // An explicit id list: synchronous, in chunks, one short transaction per job (a conflict never rolls back the others).
        List<BulkItem> items = [with(plan.JobIds!.Count)];
        foreach (JobId[] chunk in plan.JobIds.Chunk(BulkChunk))
        {
            foreach (JobId id in chunk)
            {
                items.Add(await ApplyAsync(request.Operation, id, scopes, actorId, plan.Reason, state, operationId, cancellationToken));
            }
        }

        BulkOperationReport report = Report(operationId, request.Operation, AdminOperationStatus.Completed, items, DateTimeOffset.UtcNow);
        await store.SaveBulkReportAsync(operationId, report, null, cancellationToken);
        await AuditAsync(new JobAuditEntry(request.Operation.ToString(), null, operationId, tenantScope, actorId, plan.Reason,
            $"Applied {report.Applied} of {report.Requested}"), cancellationToken);
        BulkOperationRecord? stored = await store.GetBulkOperationAsync(operationId, null, cancellationToken);
        return new BulkSubmission(BulkSubmissionKind.Completed, operationId, stored?.Report ?? report);
    }

    public async Task<BulkOperationReport?> GetBulkOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
        => (await store.GetBulkOperationAsync(operationId, Scopes(), cancellationToken))?.Report;

    public Task<JobStats> GetStatsAsync(DateTimeOffset? sinceUtc = null, CancellationToken cancellationToken = default)
        => store.GetStatsAsync(Scopes(), (sinceUtc ?? DateTimeOffset.UtcNow.AddHours(-24)).ToUniversalTime(), cancellationToken);

    public Task<IReadOnlyList<JobWorkerInfo>> GetWorkersAsync(CancellationToken cancellationToken = default)
        => store.GetWorkersAsync(options.Value.Worker.WorkerLivenessWindow, cancellationToken);

    public async Task<IReadOnlyList<RecurringScheduleState>> ListRecurringAsync(CancellationToken cancellationToken = default)
        => Scopes() is not null || recurring is null ? [] : await recurring.ListAsync(cancellationToken);

    public async Task<RecurringAdminOutcome> PauseRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken = default)
    {
        string why = Reason(reason);
        if (Scopes() is not null)
        {
            return RecurringAdminOutcome.NotFound;
        }

        RecurringAdminOutcome outcome = await store.PauseRecurringAsync(code, actorId, why, cancellationToken);
        if (outcome == RecurringAdminOutcome.Applied)
        {
            await AuditAsync(new JobAuditEntry("PauseRecurring", null, null, JobLimits.SystemTenantScope, actorId, why, $"{code}: {outcome}"), cancellationToken);
        }

        return outcome;
    }

    public async Task<RecurringAdminOutcome> ResumeRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken = default)
    {
        string why = Reason(reason);
        if (Scopes() is not null)
        {
            return RecurringAdminOutcome.NotFound;
        }

        RecurringAdminOutcome outcome = await store.ResumeRecurringAsync(code, actorId, why,
            (cron, zone, now) => Recurring.CronSchedule.Parse(cron, zone).NextAfter(now), cancellationToken);
        if (outcome == RecurringAdminOutcome.Applied)
        {
            await AuditAsync(new JobAuditEntry("ResumeRecurring", null, null, JobLimits.SystemTenantScope, actorId, why, $"{code}: {outcome}"), cancellationToken);
        }

        return outcome;
    }

    public async Task<RecurringTriggerResult> TriggerRecurringAsync(string code, Guid actorId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        string why = Reason(reason);
        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("An actor is required to trigger a recurring schedule.", nameof(actorId));
        }

        if (Scopes() is not null)
        {
            return new RecurringTriggerResult(RecurringAdminOutcome.NotFound);
        }

        string? queue = null;
        RecurringTriggerResult result = await store.TriggerRecurringAsync(code, actorId, why, schedule =>
        {
            // Built like a timed occurrence (RecurringScheduler.Plan): System tenancy, the occurrence's idempotency key, and the
            // schedule's unique key when overlap is not allowed. Unknown here (rolling deploy) = not eligible on this instance.
            if (!registry.TryGetByCode(schedule.JobType, out JobTypeRegistration? registration) || registration.Tenancy != JobTenancy.System)
            {
                return null;
            }

            queue = schedule.Queue;
            return new NewJob(
                Guid.NewGuid(), registration.Code, schedule.Queue, null, JobLimits.SystemTenantScope, schedule.Payload ?? "null",
                Recurring.RecurringScheduler.IdempotencyKeyFor(schedule.Code, schedule.DatabaseNow), IdempotencyConflict.ReturnExisting,
                schedule.AllowOverlap ? null : Recurring.RecurringScheduler.UniqueKeyFor(schedule.Code), UniqueKeyConflict.ReturnExisting,
                registration.MaxAttempts, null, null, null, null, actorId, "recurring");
        }, cancellationToken);

        if (result.Outcome == RecurringAdminOutcome.Applied)
        {
            await AuditAsync(new JobAuditEntry("TriggerRecurring", result.JobId, null, JobLimits.SystemTenantScope, actorId, why, $"{code}: {result.Outcome}"), cancellationToken);
            if (queue is not null)
            {
                signal.Notify(queue);
            }
        }

        return result;
    }

    /// <summary>
    /// Runs a filter-based bulk operation (the body of <c>NinjaQueue.Admin.BulkOperation.v1</c>): keyset-paged chunks, one
    /// transaction per job, the report and the resume cursor saved after every chunk, under the requester's stored scopes.
    /// </summary>
    internal async Task RunBulkOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        BulkOperationRecord? record = await store.GetBulkOperationAsync(operationId, null, cancellationToken);
        if (record is null || record.Report.Status is AdminOperationStatus.Completed or AdminOperationStatus.Failed)
        {
            return;
        }

        BulkState state = JsonSerializer.Deserialize<BulkState>(record.StateJson ?? "{}", StateJson) ?? throw new InvalidOperationException("The bulk operation has no state.");
        BulkJobFilter filter = state.Filter ?? throw new InvalidOperationException("The bulk operation has no filter.");
        List<BulkItem> items = [.. record.Report.Items];
        AdminOperationKind kind = record.Report.Operation;
        while (true)
        {
            IReadOnlyList<(long Id, JobId JobId)> page = await store.FindBulkJobsAsync(filter, state.Scopes, state.Cursor, BulkChunk, cancellationToken);
            foreach ((long id, JobId jobId) in page)
            {
                items.Add(await ApplyAsync(kind, jobId, state.Scopes, record.ActorId, record.Reason, state, operationId, cancellationToken));
                state = state with { Cursor = id };
            }

            bool done = page.Count < BulkChunk;
            BulkOperationReport report = Report(operationId, kind, done ? AdminOperationStatus.Completed : AdminOperationStatus.Running, items, record.Report.CreatedAtUtc);
            await store.SaveBulkReportAsync(operationId, report, JsonSerializer.Serialize(state, StateJson), cancellationToken);
            if (done)
            {
                await AuditAsync(new JobAuditEntry(kind.ToString(), null, operationId, record.TenantScope, record.ActorId, record.Reason,
                    $"Applied {report.Applied} of {report.Requested}"), cancellationToken);
                return;
            }
        }
    }

    private async Task<AdminRunResult> RunAgainAsync(JobId jobId, AdminRunRequest request, CancellationToken cancellationToken)
    {
        AdminRunResult result = await store.RunAgainAsync(jobId, Scopes(), request, null, cancellationToken);
        if (result.Outcome == AdminRunOutcome.Applied)
        {
            await AuditAsync(new JobAuditEntry(request.Mode.ToString(), jobId, null, result.TenantScope, request.ActorId, request.Reason, result.Outcome.ToString()), cancellationToken);
        }

        return result;
    }

    private async Task<BulkItem> ApplyAsync(
        AdminOperationKind kind, JobId jobId, IReadOnlyCollection<Guid>? scopes, Guid actorId, string reason, BulkState state, Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (kind == AdminOperationKind.BulkCancel)
            {
                CancelResult cancel = (await store.CancelJobAsync(jobId, scopes, actorId, reason, options.Value.EnableRunningJobCancel, IsInterruptible, operationId, cancellationToken)).Result;
                return new BulkItem(jobId, cancel switch
                {
                    CancelResult.Cancelled or CancelResult.CancelRequested or CancelResult.CancelAfterCurrentAttempt => BulkItemOutcome.Applied,
                    CancelResult.NotFound => BulkItemOutcome.NotFound,
                    _ => BulkItemOutcome.NotEligible,
                }, Error: cancel is CancelResult.AlreadyCompleted or CancelResult.AlreadyRequested or CancelResult.RunningNotSupported ? cancel.ToString() : null);
            }

            AdminRunMode mode = kind == AdminOperationKind.BulkRetry ? AdminRunMode.Retry : AdminRunMode.Requeue;
            AdminRunResult run = await store.RunAgainAsync(jobId, scopes,
                new AdminRunRequest(mode, actorId, reason, mode == AdminRunMode.Requeue ? state.RunAtUtc : null, state.IncludeContinuations, DefaultAttempts),
                operationId, cancellationToken);
            return run.Outcome switch
            {
                AdminRunOutcome.Applied => new BulkItem(jobId, BulkItemOutcome.Applied, run.Status),
                AdminRunOutcome.NotFound => new BulkItem(jobId, BulkItemOutcome.NotFound),
                AdminRunOutcome.Conflict => new BulkItem(jobId, BulkItemOutcome.Conflict, run.Status, run.HolderJobId),
                AdminRunOutcome.AttemptLimitReached => new BulkItem(jobId, BulkItemOutcome.NotEligible, run.Status, Error: run.Outcome.ToString()),
                _ => new BulkItem(jobId, BulkItemOutcome.NotEligible, run.Status),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One job's failure is reported per job and never stops the operation (§17.9).
        catch (Exception ex)
        {
            return new BulkItem(jobId, BulkItemOutcome.Failed, Error: ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }
#pragma warning restore CA1031
    }

    private NewJob BulkJob(Guid operationId, Guid tenantScope)
    {
        JobTypeRegistration type = registry.TryGetByCode(BulkOperationJobHandler.Code, out JobTypeRegistration? found)
            ? found
            : throw new InvalidOperationException($"The bulk-operation job type '{BulkOperationJobHandler.Code}' is not registered.");
        return new NewJob(
            Guid.NewGuid(), type.Code, type.Queue, null, JobLimits.SystemTenantScope,
            serializer.Serialize(new BulkOperationPayload(operationId), typeof(BulkOperationPayload)),
            null, IdempotencyConflict.ReturnExisting, $"bulk-admin:{tenantScope:N}", UniqueKeyConflict.Reject, type.MaxAttempts, null,
            operationId.ToString("N"), null, null, null, "ninjaqueue-admin");
    }

    private static BulkOperationReport Report(Guid operationId, AdminOperationKind kind, AdminOperationStatus status, List<BulkItem> items, DateTimeOffset created)
        => new(operationId, kind, status, items.Count,
            items.Count(i => i.Outcome == BulkItemOutcome.Applied), items.Count(i => i.Outcome == BulkItemOutcome.NotEligible),
            items.Count(i => i.Outcome == BulkItemOutcome.Conflict), items.Count(i => i.Outcome == BulkItemOutcome.NotFound),
            items.Count(i => i.Outcome == BulkItemOutcome.Failed), items, created, status == AdminOperationStatus.Completed ? DateTimeOffset.UtcNow : null);

    /// <summary>Validates a bulk request (§17.9); throws <see cref="JobAdminValidationException"/> for a 400.</summary>
    internal static BulkPlan Validate(BulkRequest request, Guid actorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actorId == Guid.Empty)
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.ActorRequired, "A bulk operation needs the acting user.");
        }

        if (!Enum.IsDefined(request.Operation))
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.Invalid, $"Unknown operation {request.Operation}.");
        }

        string reason = Reason(request.Reason);
        string key = request.IdempotencyKey?.Trim() ?? "";
        if (key.Length is 0 or > JobLimits.MaxKeyLength)
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.IdempotencyKeyRequired, $"An idempotency key of 1-{JobLimits.MaxKeyLength} characters is required.");
        }

        bool hasIds = request.JobIds is not null;
        if (hasIds == (request.Filter is not null) || request.JobIds is { Count: 0 })
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.BulkSelectorRequired, "Supply exactly one of jobIds (1-500) or filter.");
        }

        if (request.JobIds is { Count: > BulkRequest.MaxJobIds })
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.BulkJobIdsLimitExceeded,
                $"At most {BulkRequest.MaxJobIds} job ids per request; use a filter for larger operations.");
        }

        return new BulkPlan(reason, key, request.JobIds?.Distinct().ToList());
    }

    private static string Reason(string reason)
    {
        string trimmed = reason?.Trim() ?? "";
        return trimmed.Length == 0
            ? throw new JobAdminValidationException(JobAdminErrorCodes.ReasonRequired, "A reason is required.")
            : trimmed.Length > 1000 ? trimmed[..1000] : trimmed;
    }

    // The tenant a bulk operation is recorded under: the caller's only tenant, the filter's tenant, or the system scope.
    private static Guid OperationScope(IReadOnlyCollection<Guid>? scopes, Guid? filterTenant)
        => scopes switch
        {
            { Count: 1 } => scopes.First(),
            _ when filterTenant is { } t && (scopes is null || scopes.Contains(t)) => t,
            _ => JobLimits.SystemTenantScope,
        };

    private IReadOnlyCollection<Guid>? Scopes()
    {
        IReadOnlyCollection<Guid>? scopes = tenants?.AllowedTenantScopes;
        return scopes is { Count: > MaxTenantScopes }
            ? throw new JobAdminValidationException(JobAdminErrorCodes.TooManyTenantScopes, $"At most {MaxTenantScopes} tenant scopes per caller.")
            : scopes;
    }

    private int DefaultAttempts(string jobType)
        => registry.TryGetByCode(jobType, out JobTypeRegistration? type) ? type.MaxAttempts : options.Value.Retry.MaxAttempts;

    private bool IsInterruptible(string jobType) => !registry.TryGetByCode(jobType, out JobTypeRegistration? type) || type.Interruptible;

    private Task AuditAsync(JobAuditEntry entry, CancellationToken cancellationToken) => audit?.RecordAsync(entry, cancellationToken) ?? Task.CompletedTask;

    private static long? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        try
        {
            return BitConverter.ToInt64(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "="));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new JobAdminValidationException(JobAdminErrorCodes.InvalidCursor, "The cursor is not valid.");
        }
    }

    private static string FormatCursor(long id) => Convert.ToBase64String(BitConverter.GetBytes(id)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A validated bulk request.</summary>
    internal sealed record BulkPlan(string Reason, string IdempotencyKey, IReadOnlyList<JobId>? JobIds);

    /// <summary>What a bulk operation needs to run and resume (stored in <c>filter_json</c>).</summary>
    internal sealed record BulkState(Guid[]? Scopes, BulkJobFilter? Filter, DateTimeOffset? RunAtUtc, bool IncludeContinuations, long Cursor);
}