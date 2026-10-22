using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.Admin;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// Admin pause / resume / trigger of recurring schedules (§14.6, §16.8). The contract with the startup sync (Phase 6b): an admin pause
// always leaves paused_by non-null, so a sync never re-enables it; resume recomputes next_run_at_utc from now.
internal sealed partial class EfCoreJobStore<TContext>
{
    public Task<RecurringAdminOutcome> PauseRecurringAsync(string code, Guid? actorId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await LockScheduleForAdminAsync(sql, c, t, code, ct) is not { } schedule)
            {
                return RecurringAdminOutcome.NotFound;
            }

            if (!schedule.Enabled)
            {
                return RecurringAdminOutcome.NotEligible;
            }

            await JobCommand.Create(c, t, sql.PauseRecurring).Id(schedule.Id)
                .With("paused_by", actorId ?? JobLimits.SystemTenantScope, DbType.Guid).RunAsync(ct);
            await ScheduleEventAsync(sql, c, t, schedule, JobEventCodes.RecurringPaused, actorId, reason, null, ct);
            return RecurringAdminOutcome.Applied;
        }, cancellationToken);
    }

    public Task<RecurringAdminOutcome> ResumeRecurringAsync(
        string code, Guid? actorId, string reason, Func<string, string, DateTimeOffset, DateTimeOffset> nextAfter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(nextAfter);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await LockScheduleForAdminAsync(sql, c, t, code, ct) is not { } schedule)
            {
                return RecurringAdminOutcome.NotFound;
            }

            // Only an admin pause is resumed here; a schedule disabled because it left the code stays disabled.
            if (schedule.Enabled || schedule.PausedBy is null)
            {
                return RecurringAdminOutcome.NotEligible;
            }

            DateTimeOffset next = nextAfter(schedule.Cron, schedule.TimeZone, schedule.DatabaseNow);
            await JobCommand.Create(c, t, sql.ResumeRecurring).Id(schedule.Id).With("next_run", next, DbType.DateTimeOffset).RunAsync(ct);
            await ScheduleEventAsync(sql, c, t, schedule, JobEventCodes.RecurringResumed, actorId, reason, next, ct);
            return RecurringAdminOutcome.Applied;
        }, cancellationToken);
    }

    public Task<RecurringTriggerResult> TriggerRecurringAsync(
        string code, Guid? actorId, string reason, Func<RecurringTriggerSchedule, NewJob?> build, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(build);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await LockScheduleForAdminAsync(sql, c, t, code, ct) is not { } schedule)
            {
                return new RecurringTriggerResult(RecurringAdminOutcome.NotFound);
            }

            // Paused by an admin or disabled because it left the code: a trigger would bypass that decision.
            if (!schedule.Enabled
                || build(new RecurringTriggerSchedule(schedule.Code, schedule.JobType, schedule.Queue, schedule.Payload, schedule.AllowOverlap, schedule.DatabaseNow)) is not { } job)
            {
                return new RecurringTriggerResult(RecurringAdminOutcome.NotEligible);
            }

            // The normal enqueue path: idempotency key and, without overlap, the schedule's unique-key reservation (§15.3).
            JobId inserted = await InsertAsync(sql, c, t, job, ct);
            (long jobId, string? idempotencyKey) = await JobRefAsync(sql, c, t, inserted.Value, ct);
            if (inserted.Value != job.GuidId)
            {
                // Deduplicated: the same occurrence instant was enqueued already (idempotency), or the overlap key is held by the
                // still-active previous occurrence. Either way nothing new runs.
                return string.Equals(idempotencyKey, job.IdempotencyKey, StringComparison.Ordinal)
                    ? new RecurringTriggerResult(RecurringAdminOutcome.Applied, inserted)
                    : new RecurringTriggerResult(RecurringAdminOutcome.Conflict, ActiveJobId: inserted);
            }

            await JobCommand.Create(c, t, sql.MarkRecurringTriggered).Id(schedule.Id).With("last_job_id", jobId, DbType.Int64).RunAsync(ct);
            await InsertEventAsync(sql, c, t, jobId, JobEventCodes.RecurringTriggered, null, actorId, reason, ct,
                JsonSerializer.Serialize(new { schedule = schedule.Code, occurrenceUtc = schedule.DatabaseNow }));
            if (_options.ListenNotify.Enabled && sql.IsPostgres)
            {
                // Delivered on commit, together with the job.
                await JobCommand.Create(c, t, PostgresWakeup.NotifySql)
                    .With("channel", PostgresWakeup.Channel(sql.Schema), DbType.String)
                    .With("queue", schedule.Queue, DbType.String)
                    .ScalarAsync(ct);
            }

            return new RecurringTriggerResult(RecurringAdminOutcome.Applied, inserted);
        }, cancellationToken);
    }

    private static async Task<AdminSchedule?> LockScheduleForAdminAsync(JobSql sql, DbConnection c, DbTransaction t, string code, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, sql.LockRecurringForAdmin).With("code", code, DbType.String);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AdminSchedule(reader.ReadLong(0), reader.GetString(1), Convert.ToBoolean(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.ReadGuidOrNull(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.ReadLong(6), reader.ReadUtc(7),
                reader.GetString(8), reader.GetString(9), reader.ReadStringOrNull(10), Convert.ToBoolean(reader.GetValue(11), System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    // job_events.job_id is a non-null FK to a job: a schedule's event is written on its last job. A schedule that never fired
    // has none, so its pause/resume is recorded only in the host's audit sink (reported as a schema finding).
    private static Task<int> ScheduleEventAsync(
        JobSql sql, DbConnection c, DbTransaction t, AdminSchedule schedule, string code, Guid? actorId, string reason, DateTimeOffset? next, CancellationToken ct)
        => schedule.LastJobId is { } jobId
            ? InsertEventAsync(sql, c, t, jobId, code, null, actorId, reason, ct, JsonSerializer.Serialize(new { schedule = schedule.Code, nextRunAtUtc = next }))
            : Task.FromResult(0);

    private sealed record AdminSchedule(
        long Id, string Code, bool Enabled, Guid? PausedBy, string Cron, string TimeZone, long? LastJobId, DateTimeOffset DatabaseNow,
        string JobType, string Queue, string? Payload, bool AllowOverlap);
}