using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaQueue.EntityFrameworkCore.Sql;
using NinjaQueue.Enqueue;
using NinjaQueue.Storage;

namespace NinjaQueue.EntityFrameworkCore;

// The claim with concurrency keys and MaxRunningPerTenant slots (§14.5, Release 2).
internal sealed partial class EfCoreJobStore<TContext>
{
    /// <summary>How many keyed candidates one claim scans per job it wants (several rows can share a key).</summary>
    private const int KeyedScanFactor = 8;

    private const int MaxKeyedScan = 500;

    /// <summary>
    /// Claims unkeyed jobs first (the §15.2 batch claim), then fills the remaining capacity from keyed jobs in a separate short
    /// transaction, so a unique-violation race on a key never undoes the unkeyed claim. A lost race is retried once after a
    /// jittered 50–200 ms; a keyed claim that still fails returns what was claimed and the next poll tries again.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(
        string queue, IReadOnlyList<string> jobTypes, string workerId, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        IReadOnlyList<ClaimedJob> unkeyed = await ClaimUnkeyedAsync(queue, jobTypes, workerId, maxJobs, leaseDuration, cancellationToken);
        if (unkeyed.Count >= maxJobs || jobTypes.Count == 0)
        {
            return unkeyed;
        }

        for (int round = 0; round < 2; round++)
        {
            try
            {
                IReadOnlyList<ClaimedJob> keyed = await ClaimKeyedAsync(queue, jobTypes, workerId, maxJobs - unkeyed.Count, leaseDuration, cancellationToken);
                return keyed.Count == 0 ? unkeyed : [.. unkeyed, .. keyed];
            }
            catch (Exception ex) when (IsLostKeyRace(ex) && round == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 + RandomNumberGenerator.GetInt32(151)), cancellationToken);
            }
            catch (Exception ex) when (IsLostKeyRace(ex) || (unkeyed.Count > 0 && IsDatabaseFailure(ex)))
            {
                // A second lost race, or a failure after unkeyed jobs were leased by this call: never lose those.
                return unkeyed;
            }
        }

        return unkeyed;
    }

    private Task<IReadOnlyList<ClaimedJob>> ClaimKeyedAsync(
        string queue, IReadOnlyList<string> jobTypes, string workerId, int maxJobs, TimeSpan leaseDuration, CancellationToken cancellationToken)
        => unitOfWork.UseTransactionAsync<IReadOnlyList<ClaimedJob>>(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            JobSql sql = Sql(session.DbContext);
            DbConnection connection = session.Connection;
            DbTransaction transaction = session.Transaction!.GetDbTransaction();

            List<(long Id, Guid Scope, string Key)> candidates = [];
            await using (DbCommand scan = JobCommand.Create(connection, transaction, sql.KeyedCandidates)
                .With("queue", queue, DbType.String)
                .With("scan", Math.Min(MaxKeyedScan, maxJobs * KeyedScanFactor), DbType.Int32))
            {
                if (sql.IsPostgres)
                {
                    scan.WithInferred("types", jobTypes.ToArray());
                }
                else
                {
                    scan.With("types", JsonSerializer.Serialize(jobTypes), DbType.String);
                }

                await using DbDataReader reader = await scan.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    candidates.Add((reader.ReadLong(0), reader.GetGuid(1), reader.GetString(2)));
                }
            }

            if (candidates.Count == 0)
            {
                return [];
            }

            List<(long Id, Guid Scope, string Key)> chosen = await ChooseKeyedAsync(sql, connection, transaction, candidates, maxJobs, ct);
            List<(long Id, Guid Token, DateTimeOffset Expires)> leased = [with(chosen.Count)];

            // Leased in (tenant, key) order: two concurrent claims then take the running-key index entries in the same order,
            // so they can wait on each other (and one loses the race with a unique violation) but never deadlock.
            foreach ((long id, _, string key) in chosen.OrderBy(c => c.Scope).ThenBy(c => c.Key, StringComparer.Ordinal))
            {
                Guid token = Guid.NewGuid();
                await using DbCommand lease = JobCommand.Create(connection, transaction, sql.LeaseKeyed)
                    .Id(id)
                    .Token(token)
                    .With("worker_id", workerId, DbType.String)
                    .With("lease_ms", (int)leaseDuration.TotalMilliseconds, DbType.Int32)
                    .With("concurrency_key", key, DbType.String);
                if (await lease.ExecuteScalarAsync(ct) is { } expires and not DBNull)
                {
                    leased.Add((id, token, expires is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : ((DateTimeOffset)expires).ToUniversalTime()));
                }
            }

            if (leased.Count == 0)
            {
                return [];
            }

            // Mirrors the unkeyed claim's writes (attempt charged, attempt row, JobClaimed) for the whole set, in one round trip.
            await using DbCommand mark = JobCommand.Create(connection, transaction, sql.MarkLeased(IdList(leased.Select(x => x.Id))))
                .With("worker_id", workerId, DbType.String)
                .With("event_code", JobEventCodes.JobClaimed, DbType.String);
            return await ReadClaimedAsync(mark, workerId, ct);
        }, cancellationToken);

    /// <summary>One row per explicit key; for slot jobs, as many rows per (tenant, group) as there are free slots.</summary>
    private static async Task<List<(long Id, Guid Scope, string Key)>> ChooseKeyedAsync(
        JobSql sql, DbConnection connection, DbTransaction transaction, List<(long Id, Guid Scope, string Key)> candidates, int maxJobs, CancellationToken ct)
    {
        List<(long Id, Guid Scope, string Key)> chosen = [];
        HashSet<(Guid, string)> takenKeys = [];
        Dictionary<(Guid Scope, string Group), Queue<int>> freeSlots = [];
        foreach ((long id, Guid scope, string key) in candidates)
        {
            if (chosen.Count >= maxJobs)
            {
                break;
            }

            if (!ConcurrencyKeys.TryParseSlot(key, out string group, out int slots))
            {
                if (takenKeys.Add((scope, key)))
                {
                    chosen.Add((id, scope, key));
                }

                continue;
            }

            if (!freeSlots.TryGetValue((scope, group), out Queue<int>? free))
            {
                HashSet<string> leasedKeys = [.. await ReadStringsAsync(connection, transaction, sql.LeasedSlots, cmd => cmd
                    .With("tenant_scope", scope, DbType.Guid)
                    .With("pattern", group + "#%", DbType.String), ct)];

                free = new Queue<int>(Enumerable.Range(0, slots).Where(k => !leasedKeys.Contains(ConcurrencyKeys.SlotKey(group, k))));
                freeSlots[(scope, group)] = free;
            }

            if (free.TryDequeue(out int slot))
            {
                chosen.Add((id, scope, ConcurrencyKeys.SlotKey(group, slot)));
            }
        }

        return chosen;
    }

    /// <summary>
    /// Another claim leased the key first: a unique violation (PostgreSQL 23505; SQL Server 2601 / 2627), or a deadlock
    /// between two keyed claims (PostgreSQL 40P01; SQL Server 1205), raw or wrapped by an execution strategy.
    /// </summary>
    internal static bool IsLostKeyRace(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException db
                && (db.SqlState is "23505" or "40P01"
                    || (db.GetType().GetProperty("Number")?.GetValue(db) is int number && number is 2601 or 2627 or 1205)))
            {
                return true;
            }
        }

        return false;
    }
}