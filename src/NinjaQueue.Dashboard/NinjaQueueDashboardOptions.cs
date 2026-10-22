namespace NinjaQueue.Dashboard;

/// <summary>Dashboard settings, bound from <c>NinjaQueue:Dashboard</c>.</summary>
public sealed class NinjaQueueDashboardOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "NinjaQueue:Dashboard";

    /// <summary>
    /// The first admin, created when the dashboard has no users. They must change the password at first login; after that
    /// only the stored hash counts and these values are ignored.
    /// </summary>
    public DashboardAdminOptions Admin { get; set; } = new();

    /// <summary>How long a login lasts (default 8 hours).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Failed logins before a lockout (default 5).</summary>
    public int MaxFailedLogins { get; set; } = 5;

    /// <summary>How long a lockout lasts (default 15 minutes).</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The shortest accepted password (default 8).</summary>
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>
    /// Marks the sign-in cookie Secure (HTTPS only). Null (default): when the request is HTTPS. Set true when TLS ends at a
    /// proxy and the app sees plain HTTP without forwarded headers.
    /// </summary>
    public bool? SecureCookie { get; set; }

    /// <summary>The dashboard title shown in the header.</summary>
    public string Title { get; set; } = "NinjaQueue";
}

/// <summary>The seeded admin's credentials.</summary>
public sealed class DashboardAdminOptions
{
    /// <summary>The admin user name (default <c>admin</c>).</summary>
    public string UserName { get; set; } = "admin";

    /// <summary>The initial password (default <c>admin</c>; a change is forced at first login).</summary>
    public string Password { get; set; } = "admin";
}