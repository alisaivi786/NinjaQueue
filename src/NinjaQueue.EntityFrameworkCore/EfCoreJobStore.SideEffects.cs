using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// The external operation ledger (§15.5, Release 2). Every transaction here is short and never spans the external call.
internal sealed partial class EfCoreJobStore<TContext> : IJobSideEffectStore
{
    public Task<SideEffectBegin> BeginSideEffectAsync(ClaimedJob job, string stepKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await JobCommand.Create(c, t, sql.LeaseCheck).Id(job.Id).Token(job.LeaseToken).RunAsync(ct) != 1)
            {
                return SideEffectBegin.LeaseLost;
            }

            if (sql.SideEffectProbe is { } probe && await ReadSideEffectAsync(c, t, probe, job.Id, stepKey, ct) is { } held)
            {
                return held;
            }

            object? inserted = await JobCommand.Create(c, t, sql.InsertSideEffect).Id(job.Id).Token(job.LeaseToken)
                .With("step_key", stepKey, DbType.String).ScalarAsync(ct);
            if (inserted is not null and not DBNull)
            {
                return new SideEffectBegin(SideEffectBeginKind.Inserted, SideEffectStatus.Started, job.LeaseToken, 1, null, null);
            }

            return await ReadSideEffectAsync(c, t, sql.SelectSideEffect, job.Id, stepKey, ct)
                ?? throw new InvalidOperationException($"Side effect '{stepKey}' conflicted on insert but was not found.");
        }, cancellationToken);
    }

    public Task<SideEffectSwap> SwapSideEffectAsync(
        ClaimedJob job, string stepKey, Guid expectedOwner, Guid newOwner, SideEffectStatus status, bool countIssue, string? externalRef,
        string? resultJson, string? eventCode, string? eventData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            if (await JobCommand.Create(c, t, sql.LeaseCheck).Id(job.Id).Token(job.LeaseToken).RunAsync(ct) != 1)
            {
                return new SideEffectSwap(SideEffectSwapResult.LeaseLost, 0);
            }

            object? count = await JobCommand.Create(c, t, sql.SwapSideEffect)
                .Id(job.Id)
                .With("step_key", stepKey, DbType.String)
                .With("expected_owner", expectedOwner, DbType.Guid)
                .With("new_owner", newOwner, DbType.Guid)
                .With("status", (short)status, DbType.Int16)
                .With("count", countIssue ? 1 : 0, DbType.Int32)
                .With("external_ref", externalRef, DbType.String)
                .With("result_json", resultJson, DbType.String)
                .ScalarAsync(ct);
            if (count is null or DBNull)
            {
                return new SideEffectSwap(SideEffectSwapResult.Lost, 0);
            }

            if (eventCode is not null)
            {
                await InsertEventAsync(sql, c, t, job.Id, eventCode, job.WorkerId, null, null, ct, eventData);
            }

            return new SideEffectSwap(SideEffectSwapResult.Won, Convert.ToInt32(count, CultureInfo.InvariantCulture));
        }, cancellationToken);
    }

    public Task<bool> RecordSideEffectAsync(
        ClaimedJob job, string stepKey, SideEffectStatus status, string? externalRef, string? resultJson, string? lostOwnerEventCode, string? eventData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection c = session.Connection;
            DbTransaction t = session.Transaction!.GetDbTransaction();
            int owned = await JobCommand.Create(c, t, sql.RecordSideEffect)
                .Id(job.Id)
                .Token(job.LeaseToken)
                .With("step_key", stepKey, DbType.String)
                .With("status", (short)status, DbType.Int16)
                .With("external_ref", externalRef, DbType.String)
                .With("result_json", resultJson, DbType.String)
                .RunAsync(ct);
            if (owned == 0 && lostOwnerEventCode is not null
                && await ReadSideEffectAsync(c, t, sql.SelectSideEffect, job.Id, stepKey, ct) is { IssueCount: > 1 })
            {
                // Another worker took the step over and issued it again while this call was in flight: issued twice (§15.5 step 3).
                // A step resolved by reconcile (issue_count 1) was not issued again, so it is no duplicate.
                await InsertEventAsync(sql, c, t, job.Id, lostOwnerEventCode, job.WorkerId, null, "The step was taken over while this call was in flight.", ct, eventData);
            }

            return owned == 1;
        }, cancellationToken);
    }

    private static async Task<SideEffectBegin?> ReadSideEffectAsync(DbConnection c, DbTransaction t, string text, long jobId, string stepKey, CancellationToken ct)
    {
        await using DbCommand command = JobCommand.Create(c, t, text).Id(jobId).With("step_key", stepKey, DbType.String);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SideEffectBegin(SideEffectBeginKind.Existing, (SideEffectStatus)Convert.ToInt16(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetGuid(1),
                reader.ReadInt(2), reader.ReadStringOrNull(3), reader.ReadStringOrNull(4))
            : null;
    }
}