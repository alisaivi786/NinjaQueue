using NinjaQueue.Admin;

namespace NinjaQueue.Dashboard;

/// <summary>
/// Seeds dashboard tenants and users from the host's code (for example, one tenant per customer at onboarding). Users
/// created here must change their password at first sign-in.
/// </summary>
public sealed class NinjaQueueDashboardUsers(IJobDashboardStore store)
{
    /// <summary>Adds a tenant name, or renames it.</summary>
    public Task UpsertTenantAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return store.UpsertTenantAsync(new DashboardTenant(tenantId, name.Trim()), cancellationToken);
    }

    /// <summary>Creates the user when no user has that name; true when it was created. An existing user is left as it is.</summary>
    public async Task<bool> EnsureUserAsync(
        string userName, string initialPassword, bool isAdmin, IReadOnlyList<Guid>? tenantIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPassword);
        if (await store.FindUserByNameAsync(userName, cancellationToken) is not null)
        {
            return false;
        }

        return await store.CreateUserAsync(new DashboardUser(
            Guid.NewGuid(), userName.Trim(), DashboardPasswordHasher.Hash(initialPassword), isAdmin, MustChangePassword: true, 0, null, Guid.NewGuid(),
            isAdmin ? [] : tenantIds ?? [], DateTimeOffset.UtcNow), cancellationToken);
    }
}