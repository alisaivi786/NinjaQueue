using System.Data;
using Microsoft.EntityFrameworkCore;
using NinjaQueue.Admin;
using NinjaQueue.EntityFrameworkCore.Entities;

namespace NinjaQueue.EntityFrameworkCore;

// Dashboard users and tenant names: small tables, plain EF Core queries (no raw SQL).
internal sealed partial class EfCoreJobStore<TContext> : IJobDashboardStore
{
    public Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
        => unitOfWork.UseAsync((session, ct) => session.DbContext.Set<DashboardUserRecord>().CountAsync(ct), cancellationToken);

    public Task<DashboardUser?> FindUserByNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        string name = NormalizeUserName(userName);
        return unitOfWork.UseAsync(async (session, ct) =>
        {
            DashboardUserRecord? user = await session.DbContext.Set<DashboardUserRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.UserName == name, ct);
            return user is null ? null : await ToUserAsync(session.DbContext, user, ct);
        }, cancellationToken);
    }

    public Task<DashboardUser?> FindUserAsync(Guid id, CancellationToken cancellationToken = default)
        => unitOfWork.UseAsync(async (session, ct) =>
        {
            DashboardUserRecord? user = await session.DbContext.Set<DashboardUserRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return user is null ? null : await ToUserAsync(session.DbContext, user, ct);
        }, cancellationToken);

    public Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken cancellationToken = default)
        => unitOfWork.UseAsync<IReadOnlyList<DashboardUser>>(async (session, ct) =>
        {
            List<DashboardUserRecord> users = await session.DbContext.Set<DashboardUserRecord>().AsNoTracking().OrderBy(x => x.UserName).ToListAsync(ct);
            ILookup<Guid, Guid> tenants = (await session.DbContext.Set<DashboardUserTenantRecord>().AsNoTracking().ToListAsync(ct))
                .ToLookup(x => x.UserId, x => x.TenantId);
            return [.. users.Select(u => ToUser(u, [.. tenants[u.Id]]))];
        }, cancellationToken);

    public async Task<bool> CreateUserAsync(DashboardUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        string name = NormalizeUserName(user.UserName);
        try
        {
            return await unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
            {
                DbContext db = session.DbContext;
                if (await db.Set<DashboardUserRecord>().AnyAsync(x => x.UserName == name, ct))
                {
                    return false;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                db.Add(new DashboardUserRecord
                {
                    Id = user.Id,
                    UserName = name,
                    PasswordHash = user.PasswordHash,
                    IsAdmin = user.IsAdmin,
                    MustChangePassword = user.MustChangePassword,
                    FailedLogins = 0,
                    SecurityStamp = user.SecurityStamp,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
                await db.SaveChangesAsync(ct);
                await ReplaceTenantsAsync(db, user.Id, user.IsAdmin ? [] : user.TenantIds, ct);
                return true;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            return false; // a concurrent create took the name
        }
    }

    public Task<bool> UpdateUserAsync(DashboardUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            DbContext db = session.DbContext;
            DashboardUserRecord? row = await db.Set<DashboardUserRecord>().FirstOrDefaultAsync(x => x.Id == user.Id, ct);
            if (row is null)
            {
                return false;
            }

            row.PasswordHash = user.PasswordHash;
            row.IsAdmin = user.IsAdmin;
            row.MustChangePassword = user.MustChangePassword;
            row.FailedLogins = user.FailedLogins;
            row.LockedUntilUtc = user.LockedUntilUtc;
            row.SecurityStamp = user.SecurityStamp;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await ReplaceTenantsAsync(db, user.Id, user.IsAdmin ? [] : user.TenantIds, ct);
            return true;
        }, cancellationToken);
    }

    public Task<bool> DeleteUserAsync(Guid id, CancellationToken cancellationToken = default)
        => unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            await session.DbContext.Set<DashboardUserTenantRecord>().Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
            return await session.DbContext.Set<DashboardUserRecord>().Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0;
        }, cancellationToken);

    public Task<IReadOnlyList<DashboardTenant>> ListTenantsAsync(CancellationToken cancellationToken = default)
        => unitOfWork.UseAsync<IReadOnlyList<DashboardTenant>>(async (session, ct) =>
            [.. (await session.DbContext.Set<TenantRecord>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
                .Select(x => new DashboardTenant(x.TenantId, x.Name))], cancellationToken);

    public Task UpsertTenantAsync(DashboardTenant tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            DbContext db = session.DbContext;
            TenantRecord? row = await db.Set<TenantRecord>().FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, ct);
            if (row is null)
            {
                db.Add(new TenantRecord { TenantId = tenant.TenantId, Name = tenant.Name.Trim(), CreatedAtUtc = DateTimeOffset.UtcNow });
            }
            else
            {
                row.Name = tenant.Name.Trim();
            }

            await db.SaveChangesAsync(ct);
        }, cancellationToken);
    }

    public Task<bool> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => unitOfWork.UseTransactionAsync(IsolationLevel.ReadCommitted, async (session, ct) =>
        {
            await session.DbContext.Set<DashboardUserTenantRecord>().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct);
            return await session.DbContext.Set<TenantRecord>().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
        }, cancellationToken);

    /// <summary>Login names are compared case-insensitively on every database: they are stored and searched lowercase.</summary>
    internal static string NormalizeUserName(string userName)
        => (userName ?? string.Empty).Trim().ToLowerInvariant();

    private static async Task ReplaceTenantsAsync(DbContext db, Guid userId, IReadOnlyList<Guid> tenantIds, CancellationToken ct)
    {
        await db.Set<DashboardUserTenantRecord>().Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        foreach (Guid tenantId in tenantIds.Distinct())
        {
            db.Add(new DashboardUserTenantRecord { UserId = userId, TenantId = tenantId });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<DashboardUser> ToUserAsync(DbContext db, DashboardUserRecord user, CancellationToken ct)
        => ToUser(user, await db.Set<DashboardUserTenantRecord>().AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.TenantId).ToListAsync(ct));

    private static DashboardUser ToUser(DashboardUserRecord user, IReadOnlyList<Guid> tenantIds)
        => new(user.Id, user.UserName, user.PasswordHash, user.IsAdmin, user.MustChangePassword, user.FailedLogins, user.LockedUntilUtc,
            user.SecurityStamp, tenantIds, user.CreatedAtUtc);
}