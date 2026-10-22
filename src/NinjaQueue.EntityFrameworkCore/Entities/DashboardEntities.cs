namespace NinjaQueue.EntityFrameworkCore.Entities;

// The dashboard tables (migration 20261023000000_Dashboard): users, their tenant assignments and tenant names.

/// <summary><c>dashboard_users</c>: one row per dashboard login.</summary>
internal sealed class DashboardUserRecord
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public Guid SecurityStamp { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary><c>dashboard_user_tenants</c>: the tenants a non-admin user may see.</summary>
internal sealed class DashboardUserTenantRecord
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
}

/// <summary><c>tenants</c>: tenant names for the tenant switcher (jobs only store the tenant id).</summary>
internal sealed class TenantRecord
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}