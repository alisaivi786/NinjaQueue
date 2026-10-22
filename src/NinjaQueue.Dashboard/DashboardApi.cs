using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NinjaQueue.Admin;
using NinjaQueue.Registry;
using NinjaQueue.Storage;

namespace NinjaQueue.Dashboard;

/// <summary>The dashboard API: plain minimal-API handlers over <see cref="IJobAdminService"/> and <see cref="IJobDashboardStore"/>.</summary>
internal static partial class DashboardApi
{
    private const int MaxReasonLength = 1000;
    private const int MaxStatsWindowHours = 168;
    private const int MaxScheduleCodeLength = 200;
    private static readonly AllowPendingPasswordChange PendingChange = new();
    private static readonly AdminOnly Admin = new();

    // ---- sign-in -----------------------------------------------------------------------------------------------------

    public static void MapAuth(RouteGroupBuilder auth, string prefix)
    {
        auth.MapPost("/login", async (LoginRequest request, HttpContext http, IJobDashboardStore store, DashboardSessionCookie cookie,
            DashboardRequestContext context, IOptions<NinjaQueueDashboardOptions> options, ILoggerFactory loggers) =>
        {
            NinjaQueueDashboardOptions settings = options.Value;
            await SeedAdminAsync(store, settings, http.RequestAborted);

            string userName = (request.UserName ?? string.Empty).Trim();
            DashboardUser? user = userName.Length is 0 or > 100 ? null : await store.FindUserByNameAsync(userName, http.RequestAborted);
            if (user?.LockedUntilUtc > DateTimeOffset.UtcNow)
            {
                throw new DashboardException(StatusCodes.Status429TooManyRequests, "LockedOut", "Too many failed sign-ins. Try again later.");
            }

            if (!await DashboardPasswordHasher.VerifyAsync(request.Password ?? string.Empty, user?.PasswordHash, http.RequestAborted) || user is null)
            {
                if (user is not null)
                {
                    int failures = user.FailedLogins + 1;
                    bool lockOut = failures >= settings.MaxFailedLogins;
                    await store.UpdateUserAsync(
                        user with { FailedLogins = lockOut ? 0 : failures, LockedUntilUtc = lockOut ? DateTimeOffset.UtcNow.Add(settings.LockoutDuration) : null },
                        http.RequestAborted);
                }

                throw new DashboardException(StatusCodes.Status401Unauthorized, "InvalidCredentials", "The user name or password is wrong.");
            }

            if (user.FailedLogins != 0 || user.LockedUntilUtc is not null)
            {
                user = user with { FailedLogins = 0, LockedUntilUtc = null };
                await store.UpdateUserAsync(user, http.RequestAborted);
            }

            if (user.MustChangePassword && user.IsAdmin)
            {
                LogInitialPassword(loggers.CreateLogger("NinjaQueue.Dashboard"), user.UserName);
            }

            cookie.Issue(http, http.Request.PathBase + prefix, user, settings.SessionLifetime, settings.SecureCookie);
            context.User = user;
            return Ok(await MeAsync(user, store, settings, http.RequestAborted));
        });

        auth.MapPost("/logout", (HttpContext http) =>
        {
            DashboardSessionCookie.Clear(http, http.Request.PathBase + prefix);
            return Results.NoContent();
        });
    }

    public static void MapSession(RouteGroupBuilder api, string prefix)
    {
        api.MapGet("/auth/me", async (DashboardRequestContext context, IJobDashboardStore store, IOptions<NinjaQueueDashboardOptions> options, CancellationToken ct)
            => Ok(await MeAsync(context.User!, store, options.Value, ct))).WithMetadata(PendingChange);

        api.MapPost("/auth/change-password", async (ChangePasswordRequest request, HttpContext http, DashboardRequestContext context, IJobDashboardStore store,
            DashboardSessionCookie cookie, IOptions<NinjaQueueDashboardOptions> options) =>
        {
            DashboardUser user = context.User!;
            if (!await DashboardPasswordHasher.VerifyAsync(request.CurrentPassword ?? string.Empty, user.PasswordHash, http.RequestAborted))
            {
                throw DashboardException.BadRequest("InvalidCredentials", "The current password is wrong.");
            }

            string password = ValidatePassword(request.NewPassword, user.UserName, options.Value);
            if (DashboardPasswordHasher.Verify(password, user.PasswordHash))
            {
                throw DashboardException.BadRequest("PasswordReused", "Choose a password different from the current one.");
            }

            DashboardUser changed = user with { PasswordHash = DashboardPasswordHasher.Hash(password), MustChangePassword = false, SecurityStamp = Guid.NewGuid() };
            await store.UpdateUserAsync(changed, http.RequestAborted);
            cookie.Issue(http, http.Request.PathBase + prefix, changed, options.Value.SessionLifetime, options.Value.SecureCookie); // other sessions end with the old stamp
            return Ok(await MeAsync(changed, store, options.Value, http.RequestAborted));
        }).WithMetadata(PendingChange);

        api.MapGet("/tenants", async (DashboardRequestContext context, IJobDashboardStore store, CancellationToken ct)
            => Ok(await VisibleTenantsAsync(context.User!, store, ct)));
    }

    // ---- jobs --------------------------------------------------------------------------------------------------------

    public static void MapJobs(RouteGroupBuilder api)
    {
        api.MapGet("/jobs", async (IJobAdminService jobs, CancellationToken ct, string? status, string? jobType, string? queue, Guid? tenantId,
            DateTimeOffset? fromUtc, DateTimeOffset? toUtc, Guid? parentJobId, string? cursor, int? pageSize) =>
        {
            JobStatus? parsed = ParseStatus(status, nameof(status));
            JobPage page = await jobs.ListAsync(new JobQuery
            {
                Statuses = parsed is { } one ? [one] : null,
                JobType = Blank(jobType),
                Queue = Blank(queue),
                TenantId = tenantId,
                CreatedFromUtc = fromUtc,
                CreatedToUtc = toUtc,
                ParentJobId = parentJobId is { } parent ? new JobId(parent) : null,
                Cursor = Blank(cursor),
                PageSize = pageSize ?? 50,
            }, ct);
            return Ok(new JobPageDto([.. page.Items.Select(JobSummaryDto.From)], page.NextCursor));
        });

        api.MapGet("/jobs/{id:guid}", async (Guid id, IJobAdminService jobs, CancellationToken ct)
            => Ok(JobDetailDto.From(await FindAsync(jobs, id, includePayload: true, ct))));

        api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, JobActionRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct) =>
        {
            CancelResult result = await jobs.CancelAsync(new JobId(id), context.User!.Id, RequireReason(request?.Reason), ct);
            return result switch
            {
                CancelResult.NotFound => throw JobNotFound(id),
                CancelResult.AlreadyCompleted => throw DashboardException.Conflict("NotEligible", $"Job '{id}' has already finished.", "A finished job cannot be cancelled."),
                CancelResult.RunningNotSupported => throw DashboardException.Conflict(
                    "NotEligible", $"Job '{id}' is running and running-job cancel is turned off.", "Wait for the attempt to end, then cancel."),
                _ => Ok(new JobCancelDto(id, result.ToString())),
            };
        });

        api.MapPost("/jobs/{id:guid}/retry", async (Guid id, JobActionRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
            => Ok(Applied(id, await jobs.RetryAsync(new JobId(id), context.User!.Id, RequireReason(request?.Reason), request?.IncludeContinuations ?? true, ct))));

        api.MapPost("/jobs/{id:guid}/requeue", async (Guid id, JobRequeueRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
            => Ok(Applied(id, await jobs.RequeueAsync(
                new JobId(id), context.User!.Id, RequireReason(request?.Reason), request?.RunAtUtc, request?.IncludeContinuations ?? true, ct))));

        api.MapPost("/jobs/bulk/{operation}", BulkAsync);

        api.MapGet("/jobs/bulk/{operationId:guid}", async (Guid operationId, IJobAdminService jobs, CancellationToken ct)
            => Ok(BulkReportDto.From(await jobs.GetBulkOperationAsync(operationId, ct)
                ?? throw DashboardException.NotFound($"Bulk operation '{operationId}' was not found."))));

        api.MapGet("/jobs/stats", async (IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct, int? windowHours, Guid? tenantId) =>
        {
            int window = windowHours ?? 24;
            if (tenantId is { } picked)
            {
                context.SelectedTenant = picked; // the engine then scopes the figures to it (if the user may see it)
            }

            if (window is < 1 or > MaxStatsWindowHours)
            {
                throw DashboardException.BadRequest("ValidationFailed", $"windowHours must be between 1 and {MaxStatsWindowHours}.");
            }

            JobStats stats = await jobs.GetStatsAsync(DateTimeOffset.UtcNow.AddHours(-window), ct);
            StatusCountsDto totals = new(
                stats.Queues.Sum(q => (long)q.Enqueued), stats.Queues.Sum(q => (long)q.Scheduled), stats.Awaiting, stats.Queues.Sum(q => (long)q.Processing),
                stats.Succeeded, stats.Cancelled, stats.DeadLettered);
            long finished = totals.Succeeded + totals.DeadLettered;
            bool allTenants = context.User!.IsAdmin && context.SelectedTenant is null;
            return Ok(new StatsDto(
                [.. stats.Queues.Select(q => new QueueStatsDto(q.Queue, q.Enqueued + q.Scheduled, Math.Round(q.OldestEnqueuedAgeSeconds, 1), q.Enqueued, q.Scheduled, 0, q.Processing))],
                stats.DeadLettered,
                allTenants ? "AllTenants" : "Tenant",
                context.SelectedTenant,
                totals,
                stats.SinceUtc,
                Math.Round(finished / (double)window, 2),
                finished == 0 ? null : Math.Round(totals.DeadLettered / (double)finished, 4)));
        });

        api.MapGet("/jobs/lookups", (JobHandlerRegistry registry) =>
        {
            List<JobTypeDto> types = [.. registry.All
                .OrderBy(t => t.Code, StringComparer.Ordinal)
                .Select(t => new JobTypeDto(t.Code, t.Queue, t.Tenancy.ToString(), t.SideEffects.ToString(), t.MaxAttempts, t.DashboardVisible))];
            return Ok(new LookupsDto(types, [.. types.Select(t => t.Queue).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], Enum.GetNames<JobStatus>()));
        });
    }

    // ---- recurring schedules and workers (platform-wide: admins) ------------------------------------------------------

    public static void MapPlatform(RouteGroupBuilder api)
    {
        api.MapGet("/jobs/recurring", async (IJobAdminService jobs, CancellationToken ct)
            => Ok((await jobs.ListRecurringAsync(ct)).OrderBy(x => x.Code, StringComparer.Ordinal).Select(RecurringScheduleDto.From).ToList()))
            .WithMetadata(Admin);

        api.MapPost("/jobs/recurring/{code}/pause", (string code, RecurringActionRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
            => RecurringActionAsync(code, request, pause: true, jobs, context, ct)).WithMetadata(Admin);

        api.MapPost("/jobs/recurring/{code}/resume", (string code, RecurringActionRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
            => RecurringActionAsync(code, request, pause: false, jobs, context, ct)).WithMetadata(Admin);

        api.MapPost("/jobs/recurring/{code}/trigger", async (string code, RecurringActionRequest? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct) =>
        {
            string reason = RequireReason(request?.Reason);
            string trimmed = ScheduleCode(code);
            RecurringTriggerResult result = await jobs.TriggerRecurringAsync(trimmed, context.User!.Id, reason, ct);
            switch (result.Outcome)
            {
                case RecurringAdminOutcome.NotFound:
                    throw ScheduleNotFound(trimmed);
                case RecurringAdminOutcome.NotEligible:
                    throw DashboardException.Conflict("ScheduleNotEligible", $"Schedule '{trimmed}' is not active.", "Resume a paused schedule first.");
                case RecurringAdminOutcome.Conflict:
                    throw DashboardException.Conflict(
                        "ScheduleNotEligible", $"Schedule '{trimmed}' already has a run in progress (job {result.ActiveJobId}).", "Wait for it to finish, then try again.");
            }

            return Ok(new RecurringTriggerDto(result.JobId!.Value.Value, RecurringScheduleDto.From(await FindScheduleAsync(jobs, trimmed, ct))));
        }).WithMetadata(Admin);

        api.MapGet("/jobs/workers", async (IJobAdminService jobs, CancellationToken ct)
            => Ok((await jobs.GetWorkersAsync(ct))
                .Select(w => new WorkerDto(w.WorkerId, w.Host, w.ProcessId, w.AppVersion, string.Join(", ", w.Queues), w.StartedAtUtc, w.LastHeartbeatAtUtc))
                .ToList()))
            .WithMetadata(Admin);
    }

    // ---- users and tenants (admins) ----------------------------------------------------------------------------------

    public static void MapAdministration(RouteGroupBuilder api)
    {
        api.MapGet("/users", async (IJobDashboardStore store, CancellationToken ct)
            => Ok((await store.ListUsersAsync(ct)).Select(UserDto.From).ToList())).WithMetadata(Admin);

        api.MapPost("/users", async (CreateUserRequest request, IJobDashboardStore store, IOptions<NinjaQueueDashboardOptions> options, CancellationToken ct) =>
        {
            string userName = ValidateUserName(request.UserName);
            string password = ValidatePassword(request.Password, userName, options.Value);
            DashboardUser user = new(Guid.NewGuid(), userName, DashboardPasswordHasher.Hash(password), request.IsAdmin, MustChangePassword: true, 0, null,
                Guid.NewGuid(), await KnownTenantsAsync(store, request.TenantIds, ct), DateTimeOffset.UtcNow);
            return await store.CreateUserAsync(user, ct)
                ? Results.Json(new DashboardResponse<UserDto>(UserDto.From((await store.FindUserAsync(user.Id, ct))!)), statusCode: StatusCodes.Status201Created)
                : throw DashboardException.Conflict("UserNameTaken", $"A user named '{userName}' already exists.");
        }).WithMetadata(Admin);

        api.MapPut("/users/{id:guid}", async (Guid id, UpdateUserRequest request, DashboardRequestContext context, IJobDashboardStore store,
            IOptions<NinjaQueueDashboardOptions> options, CancellationToken ct) =>
        {
            DashboardUser user = await store.FindUserAsync(id, ct) ?? throw DashboardException.NotFound($"User '{id}' was not found.");
            if (id == context.User!.Id && !request.IsAdmin)
            {
                throw DashboardException.BadRequest("CannotDemoteSelf", "You cannot remove your own admin rights.");
            }

            DashboardUser changed = user with { IsAdmin = request.IsAdmin, TenantIds = await KnownTenantsAsync(store, request.TenantIds, ct) };
            if (!string.IsNullOrEmpty(request.Password))
            {
                // A reset by an admin: the user must choose their own password at next sign-in, and their sessions end.
                changed = changed with
                {
                    PasswordHash = DashboardPasswordHasher.Hash(ValidatePassword(request.Password, user.UserName, options.Value)),
                    MustChangePassword = true,
                    SecurityStamp = Guid.NewGuid(),
                    FailedLogins = 0,
                    LockedUntilUtc = null,
                };
            }

            await store.UpdateUserAsync(changed, ct);
            return Ok(UserDto.From((await store.FindUserAsync(id, ct))!));
        }).WithMetadata(Admin);

        api.MapDelete("/users/{id:guid}", async (Guid id, DashboardRequestContext context, IJobDashboardStore store, CancellationToken ct) =>
        {
            if (id == context.User!.Id)
            {
                throw DashboardException.BadRequest("CannotDeleteSelf", "You cannot delete your own user.");
            }

            return await store.DeleteUserAsync(id, ct) ? Results.NoContent() : throw DashboardException.NotFound($"User '{id}' was not found.");
        }).WithMetadata(Admin);

        api.MapPost("/tenants", async (UpsertTenantRequest request, IJobDashboardStore store, CancellationToken ct) =>
        {
            string name = (request.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 200)
            {
                throw DashboardException.BadRequest("ValidationFailed", "A tenant name of 1-200 characters is required.");
            }

            DashboardTenant tenant = new(request.TenantId is { } id && id != Guid.Empty ? id : Guid.NewGuid(), name);
            await store.UpsertTenantAsync(tenant, ct);
            return Ok(new TenantDto(tenant.TenantId, tenant.Name));
        }).WithMetadata(Admin);

        api.MapDelete("/tenants/{id:guid}", async (Guid id, IJobDashboardStore store, CancellationToken ct)
            => await store.DeleteTenantAsync(id, ct) ? Results.NoContent() : throw DashboardException.NotFound($"Tenant '{id}' was not found."))
            .WithMetadata(Admin);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static async Task<IResult> BulkAsync(string operation, BulkRequestDto? request, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
    {
        AdminOperationKind kind = operation.ToUpperInvariant() switch
        {
            "RETRY" => AdminOperationKind.BulkRetry,
            "REQUEUE" => AdminOperationKind.BulkRequeue,
            "CANCEL" => AdminOperationKind.BulkCancel,
            _ => throw DashboardException.NotFound($"Unknown bulk operation '{operation}'."),
        };
        string reason = RequireReason(request?.Reason);
        bool hasIds = request!.JobIds is { Count: > 0 };
        if (hasIds == request.Filter is not null)
        {
            throw DashboardException.BadRequest("BulkSelectorInvalid", "Send either jobIds or a filter, not both and not neither.");
        }

        if (hasIds && request.JobIds!.Count > BulkRequest.MaxJobIds)
        {
            throw DashboardException.BadRequest("BulkIdsLimitExceeded", $"At most {BulkRequest.MaxJobIds} job ids per request.", "Use a filter for larger operations.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw DashboardException.BadRequest("ValidationFailed", "An idempotency key is required.");
        }

        BulkSubmission submission = await jobs.BulkAsync(new BulkRequest
        {
            Operation = kind,
            JobIds = hasIds ? [.. request.JobIds!.Distinct().Select(x => new JobId(x))] : null,
            Filter = request.Filter is { } filter
                ? new BulkJobFilter
                {
                    Status = ParseStatus(filter.Status, "filter.status"),
                    JobType = Blank(filter.JobType),
                    Queue = Blank(filter.Queue),
                    TenantId = filter.TenantId,
                    FromUtc = filter.FromUtc,
                    ToUtc = filter.ToUtc,
                }
                : null,
            Reason = reason,
            IdempotencyKey = request.IdempotencyKey.Trim(),
            RunAtUtc = request.RunAtUtc,
            IncludeContinuations = request.IncludeContinuations,
        }, context.User!.Id, ct);

        return submission.Kind switch
        {
            BulkSubmissionKind.RejectedActiveOperation => throw DashboardException.Conflict(
                "BulkOperationActive", "Another bulk operation is still running.", "Wait for it to finish, then try again."),
            BulkSubmissionKind.Queued => Results.Json(
                new DashboardResponse<BulkReportDto>(submission.Report is { } queued
                    ? BulkReportDto.From(queued)
                    : new BulkReportDto(submission.OperationId!.Value, kind.ToString(), nameof(AdminOperationStatus.Pending), false, 0, 0, 0, 0, 0, 0, [], DateTimeOffset.UtcNow, null)),
                statusCode: StatusCodes.Status202Accepted),
            _ => Ok(BulkReportDto.From(submission.Report!, submission.Kind == BulkSubmissionKind.Replayed)),
        };
    }

    private static async Task<IResult> RecurringActionAsync(
        string code, RecurringActionRequest? request, bool pause, IJobAdminService jobs, DashboardRequestContext context, CancellationToken ct)
    {
        string reason = RequireReason(request?.Reason);
        string trimmed = ScheduleCode(code);
        RecurringAdminOutcome outcome = pause
            ? await jobs.PauseRecurringAsync(trimmed, context.User!.Id, reason, ct)
            : await jobs.ResumeRecurringAsync(trimmed, context.User!.Id, reason, ct);
        return outcome switch
        {
            RecurringAdminOutcome.NotFound => throw ScheduleNotFound(trimmed),
            RecurringAdminOutcome.NotEligible => throw DashboardException.Conflict(
                "ScheduleNotEligible",
                pause ? $"Schedule '{trimmed}' is not active." : $"Schedule '{trimmed}' is not paused by an administrator.",
                pause ? "Only an active schedule can be paused." : "A schedule removed from code cannot be resumed."),
            _ => Ok(RecurringScheduleDto.From(await FindScheduleAsync(jobs, trimmed, ct))),
        };
    }

    private static async Task SeedAdminAsync(IJobDashboardStore store, NinjaQueueDashboardOptions settings, CancellationToken ct)
    {
        if (await store.CountUsersAsync(ct) > 0)
        {
            return;
        }

        string userName = string.IsNullOrWhiteSpace(settings.Admin.UserName) ? "admin" : settings.Admin.UserName.Trim();
        string password = string.IsNullOrEmpty(settings.Admin.Password) ? "admin" : settings.Admin.Password;
        await store.CreateUserAsync(new DashboardUser(
            Guid.NewGuid(), userName, DashboardPasswordHasher.Hash(password), IsAdmin: true, MustChangePassword: true, 0, null, Guid.NewGuid(), [],
            DateTimeOffset.UtcNow), ct); // a concurrent first login may create it first: CreateUserAsync then returns false
    }

    private static async Task<MeDto> MeAsync(DashboardUser user, IJobDashboardStore store, NinjaQueueDashboardOptions settings, CancellationToken ct)
        => new(user.Id, user.UserName, user.IsAdmin, user.MustChangePassword, await VisibleTenantsAsync(user, store, ct), settings.Title);

    /// <summary>An admin sees every named tenant plus system jobs; anyone else sees their assigned tenants.</summary>
    private static async Task<IReadOnlyList<TenantDto>> VisibleTenantsAsync(DashboardUser user, IJobDashboardStore store, CancellationToken ct)
    {
        IReadOnlyList<DashboardTenant> named = await store.ListTenantsAsync(ct);
        if (user.IsAdmin)
        {
            return [new TenantDto(JobLimits.SystemTenantScope, "System jobs"), .. named.Select(t => new TenantDto(t.TenantId, t.Name))];
        }

        Dictionary<Guid, string> names = named.ToDictionary(t => t.TenantId, t => t.Name);
        return [.. user.TenantIds.Select(id => new TenantDto(id, names.TryGetValue(id, out string? name) ? name : id.ToString()))];
    }

    /// <summary>Only tenants the dashboard knows can be assigned (the foreign key requires it).</summary>
    private static async Task<IReadOnlyList<Guid>> KnownTenantsAsync(IJobDashboardStore store, IReadOnlyList<Guid>? requested, CancellationToken ct)
    {
        if (requested is not { Count: > 0 })
        {
            return [];
        }

        HashSet<Guid> known = [.. (await store.ListTenantsAsync(ct)).Select(t => t.TenantId)];
        Guid[] unknown = [.. requested.Where(id => !known.Contains(id))];
        return unknown.Length > 0
            ? throw DashboardException.BadRequest("UnknownTenant", $"Unknown tenant(s): {string.Join(", ", unknown)}.", "Add the tenant first.")
            : [.. requested.Distinct()];
    }

    private static string ValidateUserName(string? userName)
    {
        string trimmed = (userName ?? string.Empty).Trim();
        return UserNamePattern().IsMatch(trimmed)
            ? trimmed
            : throw DashboardException.BadRequest("ValidationFailed", "A user name is 3-100 characters: letters, digits, '.', '_', '-' or '@'.");
    }

    private static string ValidatePassword(string? password, string userName, NinjaQueueDashboardOptions settings)
    {
        if (string.IsNullOrEmpty(password) || password.Length < settings.MinPasswordLength || password.Length > 200)
        {
            throw DashboardException.BadRequest("WeakPassword", $"A password is {settings.MinPasswordLength}-200 characters.");
        }

        if (string.Equals(password, userName, StringComparison.OrdinalIgnoreCase) || string.Equals(password, settings.Admin.Password, StringComparison.Ordinal)
            || string.Equals(password, "admin", StringComparison.OrdinalIgnoreCase))
        {
            throw DashboardException.BadRequest("WeakPassword", "The password cannot be the user name or the initial admin password.");
        }

        return password;
    }

    private static async Task<JobDetail> FindAsync(IJobAdminService jobs, Guid id, bool includePayload, CancellationToken ct)
        => await jobs.GetAsync(new JobId(id), includePayload, ct) ?? throw JobNotFound(id);

    private static async Task<RecurringScheduleState> FindScheduleAsync(IJobAdminService jobs, string code, CancellationToken ct)
        => (await jobs.ListRecurringAsync(ct)).FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal)) ?? throw ScheduleNotFound(code);

    private static JobRunDto Applied(Guid id, AdminRunResult result) => result.Outcome switch
    {
        AdminRunOutcome.Applied => new JobRunDto(id, result.Outcome.ToString(), result.Status?.ToString(), result.RestoredContinuations, result.ContinuationConflicts),
        AdminRunOutcome.NotFound => throw JobNotFound(id),
        AdminRunOutcome.NotEligible => throw DashboardException.Conflict(
            "NotEligible", $"Job '{id}' is {result.Status} and cannot be run again this way.",
            "Retry applies to dead-lettered jobs; requeue to dead-lettered or cancelled jobs. A succeeded job is never re-run."),
        AdminRunOutcome.Conflict => throw DashboardException.Conflict(
            "UniqueKeyConflict", $"Job '{id}' cannot run again while job '{result.HolderJobId}' holds its unique key.", "Wait for that job to finish, or cancel it."),
        _ => throw DashboardException.Conflict("AttemptLimitReached", $"Job '{id}' has reached the attempt ceiling.", "Enqueue a new job instead."),
    };

    private static string ScheduleCode(string? code)
    {
        string trimmed = code?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > MaxScheduleCodeLength ? throw ScheduleNotFound(trimmed) : trimmed;
    }

    private static string RequireReason(string? reason)
    {
        string trimmed = reason?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? throw DashboardException.BadRequest("ReasonRequired", "A reason is required.", "Every action needs a reason, recorded in the job's history.")
            : trimmed.Length > MaxReasonLength
                ? throw DashboardException.BadRequest("ValidationFailed", $"The reason is longer than {MaxReasonLength} characters.")
                : trimmed;
    }

    private static JobStatus? ParseStatus(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.TryParse(value, ignoreCase: true, out JobStatus status) && Enum.IsDefined(status)
                ? status
                : throw DashboardException.BadRequest("ValidationFailed", $"'{value}' is not a job status.", $"{field} must be one of: {string.Join(", ", Enum.GetNames<JobStatus>())}.");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DashboardException JobNotFound(Guid id) => DashboardException.NotFound($"Job '{id}' was not found.");

    private static DashboardException ScheduleNotFound(string code) => DashboardException.NotFound($"Recurring schedule '{code}' was not found.");

    private static IResult Ok<T>(T data) => Results.Ok(new DashboardResponse<T>(data));

    [GeneratedRegex(@"^[A-Za-z0-9._@-]{3,100}$")]
    private static partial Regex UserNamePattern();

    [LoggerMessage(EventId = 7450, Level = LogLevel.Warning,
        Message = "NinjaQueue dashboard admin '{UserName}' signed in with the initial password; it must be changed before the dashboard can be used")]
    private static partial void LogInitialPassword(ILogger logger, string userName);
}