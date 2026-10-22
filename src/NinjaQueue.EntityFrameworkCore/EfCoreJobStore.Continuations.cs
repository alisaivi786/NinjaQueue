using System.Data;
using System.Data.Common;
using System.Globalization;
using NinjaQueue.Admin;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// Continuations (§15.6, Release 2).
internal sealed partial class EfCoreJobStore<TContext>
{
    /// <summary>
    /// Writes a continuation. The parent row is locked first (every parent outcome transaction updates that row), so either
    /// the parent's outcome transaction sees this child, or this transaction sees the parent's final status: no third order.
    /// The child is written through the normal insert (idempotency and unique-key reservation included), then linked with its
    /// fate: Awaiting (no queue row), released at once, or cancelled at once.
    /// </summary>
    private static async Task<JobId> InsertContinuationAsync(JobSql sql, DbConnection connection, DbTransaction transaction, NewJob job, CancellationToken ct)
    {
        long parentId;
        JobStatus parentStatus;
        await using (DbCommand lockParent = JobCommand.Create(connection, transaction, sql.LockParent).With("guid_id", job.ContinueAfter!.Value.Value, DbType.Guid))
        await using (DbDataReader reader = await lockParent.ExecuteReaderAsync(ct))
        {
            // A parent of another tenant is reported exactly like a missing one, so its existence never leaks.
            if (!await reader.ReadAsync(ct) || reader.GetGuid(2) != job.TenantScope)
            {
                throw new ArgumentException($"ContinueAfter: job {job.ContinueAfter} was not found in this tenant.", nameof(job));
            }

            parentId = reader.ReadLong(0);
            parentStatus = (JobStatus)Convert.ToInt16(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        JobId inserted = await InsertJobAsync(sql, connection, transaction, job, ct);
        if (inserted.Value != job.GuidId)
        {
            // Deduplicated (idempotency or unique key): the existing job is returned untouched.
            return inserted;
        }

        long childId = Convert.ToInt64(await JobCommand.Create(connection, transaction, sql.JobIdByGuid).With("guid_id", job.GuidId, DbType.Guid).ScalarAsync(ct),
            CultureInfo.InvariantCulture);
        ContinuationDecision decision = ContinuationRules.Decide(parentStatus, job.ContinuationPolicy);
        string ids = childId.ToString(CultureInfo.InvariantCulture);

        // The insert made it Enqueued/Scheduled with a queue row; Await and Cancel take the row away again.
        JobStatus status = decision switch
        {
            ContinuationDecision.Await => JobStatus.Awaiting,
            ContinuationDecision.Cancel => JobStatus.Cancelled,
            _ => await JobStatusAsync(connection, transaction, sql, childId, ct),
        };
        JobTerminalReason? reason = decision == ContinuationDecision.Cancel ? ContinuationRules.CancelReason(parentStatus) : null;
        if (decision != ContinuationDecision.Release)
        {
            await JobCommand.Create(connection, transaction, sql.DeleteUnleasedQueueRows(ids)).RunAsync(ct);
        }

        await JobCommand.Create(connection, transaction, sql.LinkContinuation)
            .Id(childId)
            .With("parent_id", parentId, DbType.Int64)
            .With("policy", (short)job.ContinuationPolicy, DbType.Int16)
            .With("status", (short)status, DbType.Int16)
            .With("terminal_reason", reason is { } r ? (short)r : null, DbType.Int16)
            .RunAsync(ct);

        if (decision == ContinuationDecision.Release)
        {
            await ChildEventsAsync(sql, connection, transaction, ids, JobEventCodes.ContinuationReleased, null, ct);
        }
        else if (decision == ContinuationDecision.Cancel)
        {
            await JobCommand.Create(connection, transaction, sql.DeleteActiveKeys(ids)).RunAsync(ct);
            await ChildEventsAsync(sql, connection, transaction, ids, JobEventCodes.ContinuationCancelled, reason.ToString(), ct);
        }

        return inserted;
    }

    /// <summary>
    /// Runs in every parent outcome transaction that ends a job (fenced completion, EndAsync, admin cancel): releases or
    /// cancels its Awaiting children per §15.6, and cascades a cancel down the chain (a cancelled child's own OnParentSuccess
    /// children are cancelled, its OnParentCompletion children released). Set-based per level; ids come from the database.
    /// </summary>
    internal static async Task ReleaseContinuationsAsync(JobSql sql, DbConnection connection, DbTransaction transaction, long parentId, JobStatus parentStatus, CancellationToken ct)
    {
        Queue<(List<long> Parents, JobStatus Status)> levels = new();
        levels.Enqueue(([parentId], parentStatus));
        while (levels.TryDequeue(out (List<long> Parents, JobStatus Status) level))
        {
            List<long> cancelled = [];
            foreach (long parent in level.Parents)
            {
                List<long> released = await ReadLongsAsync(connection, transaction, sql.ReleaseChildren, cmd => cmd
                    .With("parent_id", parent, DbType.Int64)
                    .With("release_all", level.Status == JobStatus.Succeeded ? 1 : 0, DbType.Int32), ct);
                foreach (long[] chunk in released.Chunk(1000))
                {
                    string ids = IdList(chunk);
                    await ExecAsync(connection, transaction, sql.InsertQueueRows(ids), ct);
                    await ChildEventsAsync(sql, connection, transaction, ids, JobEventCodes.ContinuationReleased, null, ct);
                }

                if (level.Status == JobStatus.Succeeded)
                {
                    continue;
                }

                JobTerminalReason reason = ContinuationRules.CancelReason(level.Status);
                List<long> ended = await ReadLongsAsync(connection, transaction, sql.CancelChildren, cmd => cmd
                    .With("parent_id", parent, DbType.Int64)
                    .With("terminal_reason", (short)reason, DbType.Int16), ct);
                foreach (long[] chunk in ended.Chunk(1000))
                {
                    string ids = IdList(chunk);
                    await ExecAsync(connection, transaction, sql.DeleteActiveKeys(ids), ct);
                    await ChildEventsAsync(sql, connection, transaction, ids, JobEventCodes.ContinuationCancelled, reason.ToString(), ct);
                }

                cancelled.AddRange(ended);
            }

            if (cancelled.Count > 0)
            {
                levels.Enqueue((cancelled, JobStatus.Cancelled));
            }
        }
    }

    private static Task<int> ChildEventsAsync(JobSql sql, DbConnection connection, DbTransaction transaction, string ids, string code, string? reason, CancellationToken ct,
        Guid? actorId = null, string? data = null)
        => JobCommand.Create(connection, transaction, sql.EventsFor(ids))
            .With("event_code", code, DbType.String)
            .With("actor_id", actorId, DbType.Guid)
            .With("reason", Truncate(reason, 1000), DbType.String)
            .With("data", data, DbType.String)
            .RunAsync(ct);

    private static async Task<JobStatus> JobStatusAsync(DbConnection connection, DbTransaction transaction, JobSql sql, long id, CancellationToken ct)
        => (JobStatus)Convert.ToInt16(await JobCommand.Create(connection, transaction, $"SELECT status FROM {sql.Schema}.background_jobs WHERE id = @id").Id(id).ScalarAsync(ct),
            CultureInfo.InvariantCulture);

    private static async Task<int> ExecAsync(DbConnection connection, DbTransaction? transaction, string text, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(connection, transaction, text);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static string IdList(IEnumerable<long> ids) => string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
}