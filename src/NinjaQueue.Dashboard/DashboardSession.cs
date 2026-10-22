using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using NinjaQueue.Admin;

namespace NinjaQueue.Dashboard;

/// <summary>The signed-in dashboard user and the tenant they picked, for one request.</summary>
internal sealed class DashboardRequestContext
{
    public DashboardUser? User { get; set; }

    /// <summary>The tenant chosen in the tenant switcher (null: every tenant the user may see).</summary>
    public Guid? SelectedTenant { get; set; }
}

/// <summary>
/// Scopes the engine's admin queries to the signed-in user: an admin sees every tenant (or the one they picked), anyone else
/// only their assigned tenants. Outside a dashboard request it defers to the host's own accessor, if it registered one.
/// </summary>
internal sealed class DashboardTenantAccessor(DashboardRequestContext context, IJobTenantAccessor? inner) : IJobTenantAccessor
{
    public IReadOnlyCollection<Guid>? AllowedTenantScopes
    {
        get
        {
            if (context.User is not { } user)
            {
                return inner?.AllowedTenantScopes;
            }

            IReadOnlyCollection<Guid>? allowed = user.IsAdmin ? null : user.TenantIds;
            if (context.SelectedTenant is not { } selected)
            {
                return allowed;
            }

            // A tenant outside the user's own is an empty scope: nothing is visible, never an error that leaks its existence.
            return allowed is null || allowed.Contains(selected) ? [selected] : [];
        }
    }
}

/// <summary>The login cookie: the user id, security stamp and expiry, encrypted and signed with ASP.NET Core Data Protection.</summary>
internal sealed class DashboardSessionCookie(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "nq.session";
    private readonly IDataProtector _protector = dataProtection.CreateProtector("NinjaQueue.Dashboard.Session.v1");

    public void Issue(HttpContext http, string path, DashboardUser user, TimeSpan lifetime, bool? secure = null)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow.Add(lifetime);
        string payload = string.Join('|', user.Id.ToString("N"), user.SecurityStamp.ToString("N"),
            expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        http.Response.Cookies.Append(CookieName, _protector.Protect(payload), Options(http, path, expires, secure));
    }

    public static void Clear(HttpContext http, string path)
        => http.Response.Cookies.Delete(CookieName, Options(http, path, null, null));

    /// <summary>The user id and security stamp of a valid, unexpired cookie, or null.</summary>
    public (Guid UserId, Guid Stamp)? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out string? value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            string[] parts = _protector.Unprotect(value).Split('|');
            if (parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out Guid userId) && Guid.TryParseExact(parts[1], "N", out Guid stamp)
                && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long expires)
                && DateTimeOffset.FromUnixTimeSeconds(expires) > DateTimeOffset.UtcNow)
            {
                return (userId, stamp);
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Tampered, or encrypted with a key this instance does not have: treat as signed out.
        }

        return null;
    }

    private static CookieOptions Options(HttpContext http, string path, DateTimeOffset? expires, bool? secure) => new()
    {
        HttpOnly = true,
        Secure = secure ?? http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = string.IsNullOrEmpty(path) ? "/" : path,
        Expires = expires,
        IsEssential = true,
    };
}