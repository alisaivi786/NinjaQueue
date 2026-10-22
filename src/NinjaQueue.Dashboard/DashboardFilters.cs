using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NinjaQueue.Admin;

namespace NinjaQueue.Dashboard;

/// <summary>A failure the API returns as <c>{ "error": ... }</c> with its HTTP status.</summary>
internal sealed class DashboardException : Exception
{
    public DashboardException(int status, string code, string message, string? hint = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Hint = hint;
    }

    public DashboardException()
        : this(StatusCodes.Status500InternalServerError, "Error", "The request failed.")
    {
    }

    public DashboardException(string message)
        : this(StatusCodes.Status500InternalServerError, "Error", message)
    {
    }

    public DashboardException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = StatusCodes.Status500InternalServerError;
        Code = "Error";
    }

    public int Status { get; }

    public string Code { get; }

    public string? Hint { get; }

    public static DashboardException BadRequest(string code, string message, string? hint = null) => new(StatusCodes.Status400BadRequest, code, message, hint);

    public static DashboardException NotFound(string message) => new(StatusCodes.Status404NotFound, "NotFound", message);

    public static DashboardException Conflict(string code, string message, string? hint = null) => new(StatusCodes.Status409Conflict, code, message, hint);

    public static DashboardException Forbidden(string code, string message, string? hint = null) => new(StatusCodes.Status403Forbidden, code, message, hint);
}

/// <summary>Marks an endpoint that a user who still has to change their password may call.</summary>
internal sealed class AllowPendingPasswordChange;

/// <summary>Marks an admin-only endpoint.</summary>
internal sealed class AdminOnly;

internal static class DashboardFilters
{
    /// <summary>Every API call: no caching, the CSRF header on state changes, and errors as the error envelope.</summary>
    public static async ValueTask<object?> ApiAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && http.Request.Headers[NinjaQueueDashboardExtensions.CsrfHeader] != "1")
        {
            return Error(StatusCodes.Status403Forbidden, "CsrfHeaderMissing", $"State-changing requests need the {NinjaQueueDashboardExtensions.CsrfHeader} header.");
        }

        try
        {
            return await next(context);
        }
        catch (DashboardException exception)
        {
            return Error(exception.Status, exception.Code, exception.Message, exception.Hint);
        }
        catch (JobAdminValidationException exception)
        {
            return Error(StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
    }

    /// <summary>A valid login cookie, a current security stamp, no lockout; then the password-change and admin gates.</summary>
    public static async ValueTask<object?> AuthenticateAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        IServiceProvider services = http.RequestServices;
        if (services.GetRequiredService<DashboardSessionCookie>().Read(http) is not { } session)
        {
            return Error(StatusCodes.Status401Unauthorized, "Unauthenticated", "Sign in to the dashboard.");
        }

        DashboardUser? user = await services.GetRequiredService<IJobDashboardStore>().FindUserAsync(session.UserId, http.RequestAborted);
        if (user is null || user.SecurityStamp != session.Stamp || user.LockedUntilUtc > DateTimeOffset.UtcNow)
        {
            return Error(StatusCodes.Status401Unauthorized, "Unauthenticated", "Your session has ended. Sign in again.");
        }

        DashboardRequestContext request = services.GetRequiredService<DashboardRequestContext>();
        request.User = user;
        if (Guid.TryParse(http.Request.Headers[NinjaQueueDashboardExtensions.TenantHeader], out Guid tenant))
        {
            request.SelectedTenant = tenant;
        }

        EndpointMetadataCollection? metadata = http.GetEndpoint()?.Metadata;
        if (user.MustChangePassword && metadata?.GetMetadata<AllowPendingPasswordChange>() is null)
        {
            return Error(StatusCodes.Status403Forbidden, "PasswordChangeRequired", "Change your password to continue.");
        }

        if (!user.IsAdmin && metadata?.GetMetadata<AdminOnly>() is not null)
        {
            return Error(StatusCodes.Status403Forbidden, "AdminOnly", "Only a dashboard admin can do this.");
        }

        return await next(context);
    }

    public static IResult Error(int status, string code, string message, string? hint = null)
        => Results.Json(new DashboardErrorResponse(new DashboardError(code, message, hint)), statusCode: status);
}