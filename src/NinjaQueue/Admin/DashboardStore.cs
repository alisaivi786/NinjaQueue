namespace NinjaQueue.Admin;

/// <summary>A dashboard user. An admin sees every tenant; any other user sees only the tenants in <see cref="TenantIds"/>.</summary>
/// <param name="Id">The user id (also the actor id written to job events).</param>
/// <param name="UserName">The unique login name (case-insensitive, stored lowercase).</param>
/// <param name="PasswordHash">The password hash (never the password).</param>
/// <param name="IsAdmin">True: every tenant, recurring schedules, workers and user management.</param>
/// <param name="MustChangePassword">True until the user sets their own password (the seeded admin starts with it set).</param>
/// <param name="FailedLogins">Failed logins since the last successful one.</param>
/// <param name="LockedUntilUtc">Locked out until this time after too many failed logins.</param>
/// <param name="SecurityStamp">Changes on every password change; a session with an older stamp is rejected.</param>
/// <param name="TenantIds">The tenants a non-admin user may see.</param>
/// <param name="CreatedAtUtc">When the user was created.</param>
public sealed record DashboardUser(
    Guid Id,
    string UserName,
    string PasswordHash,
    bool IsAdmin,
    bool MustChangePassword,
    int FailedLogins,
    DateTimeOffset? LockedUntilUtc,
    Guid SecurityStamp,
    IReadOnlyList<Guid> TenantIds,
    DateTimeOffset CreatedAtUtc);

/// <summary>A named tenant, for the dashboard's tenant switcher and user assignment.</summary>
public sealed record DashboardTenant(Guid TenantId, string Name);

/// <summary>Storage for dashboard users and tenant names, in the NinjaQueue schema.</summary>
public interface IJobDashboardStore
{
    /// <summary>The number of users.</summary>
    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>A user by login name (case-insensitive), or null.</summary>
    Task<DashboardUser?> FindUserByNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>A user by id, or null.</summary>
    Task<DashboardUser?> FindUserAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every user, by name.</summary>
    Task<IReadOnlyList<DashboardUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a user; false when the name is taken.</summary>
    Task<bool> CreateUserAsync(DashboardUser user, CancellationToken cancellationToken = default);

    /// <summary>Replaces a user's mutable fields and tenant list; false when the user does not exist.</summary>
    Task<bool> UpdateUserAsync(DashboardUser user, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user; false when the user does not exist.</summary>
    Task<bool> DeleteUserAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every named tenant, by name.</summary>
    Task<IReadOnlyList<DashboardTenant>> ListTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a tenant or renames it.</summary>
    Task UpsertTenantAsync(DashboardTenant tenant, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tenant name and its user assignments (its jobs are not touched); false when it does not exist.</summary>
    Task<bool> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);
}