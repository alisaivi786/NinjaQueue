using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NinjaQueue.Admin;
using NinjaQueue.Dashboard;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds and maps the NinjaQueue dashboard: the web UI and its HTTP API.</summary>
public static class NinjaQueueDashboardExtensions
{
    /// <summary>The header every state-changing API call must send (with SameSite=Strict cookies, this blocks cross-site requests).</summary>
    public const string CsrfHeader = "X-NinjaQueue-Csrf";

    /// <summary>The header the UI sends with the tenant picked in the tenant switcher.</summary>
    public const string TenantHeader = "X-NinjaQueue-Tenant";

    /// <summary>
    /// Registers the dashboard. Settings come from <c>NinjaQueue:Dashboard</c> (default admin <c>admin</c> / <c>admin</c>,
    /// changed at first login). Call after <c>AddNinjaQueue(...)</c>.
    /// </summary>
    public static IServiceCollection AddNinjaQueueDashboard(this IServiceCollection services, Action<NinjaQueueDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        OptionsBuilder<NinjaQueueDashboardOptions> options = services.AddOptions<NinjaQueueDashboardOptions>().BindConfiguration(NinjaQueueDashboardOptions.SectionName);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddDataProtection();
        services.TryAddSingleton<DashboardSessionCookie>();
        services.TryAddScoped<DashboardRequestContext>();
        services.TryAddScoped<NinjaQueueDashboardUsers>();

        // Scope the engine's admin queries to the signed-in user, keeping any accessor the host registered for its own use.
        if (!services.Any(d => d.ServiceType == typeof(DashboardTenantAccessor)))
        {
            ServiceDescriptor? hostAccessor = services.LastOrDefault(d => d.ServiceType == typeof(IJobTenantAccessor));
            if (hostAccessor is not null)
            {
                services.Remove(hostAccessor);
            }

            services.AddScoped(sp => new DashboardTenantAccessor(sp.GetRequiredService<DashboardRequestContext>(), Resolve(sp, hostAccessor)));
            services.AddScoped<IJobTenantAccessor>(sp => sp.GetRequiredService<DashboardTenantAccessor>());
        }

        return services;
    }

    /// <summary>
    /// Maps the dashboard UI at <paramref name="pattern"/> and its API at <paramref name="pattern"/>/api. Everything is behind
    /// the dashboard's own login; the host's authentication is not involved.
    /// </summary>
    public static RouteGroupBuilder MapNinjaQueueDashboard(this IEndpointRouteBuilder endpoints, string pattern = "/jobs")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        string prefix = "/" + (pattern ?? string.Empty).Trim().Trim('/');
        RouteGroupBuilder dashboard = endpoints.MapGroup(prefix);

        RouteGroupBuilder api = dashboard.MapGroup("/api");
        api.AddEndpointFilter(DashboardFilters.ApiAsync);
        DashboardApi.MapAuth(api.MapGroup("/auth"), prefix);

        RouteGroupBuilder secured = api.MapGroup(string.Empty);
        secured.AddEndpointFilter(DashboardFilters.AuthenticateAsync);
        DashboardApi.MapSession(secured, prefix);
        DashboardApi.MapJobs(secured);
        DashboardApi.MapPlatform(secured);
        DashboardApi.MapAdministration(secured);

        // "/jobs" and "/jobs/" both match here: only the form without the slash redirects (relative asset URLs need it).
        dashboard.MapGet(string.Empty, (HttpContext http) => http.Request.Path.Value?.EndsWith('/') == true
            ? DashboardUi.Serve(http, null)
            : Results.Redirect(http.Request.PathBase + prefix + "/")).ExcludeFromDescription();
        dashboard.MapGet("/{**path}", (HttpContext http, string? path) => DashboardUi.Serve(http, path)).ExcludeFromDescription();
        return dashboard;
    }

    private static IJobTenantAccessor? Resolve(IServiceProvider services, ServiceDescriptor? descriptor) => descriptor switch
    {
        null => null,
        { ImplementationInstance: IJobTenantAccessor instance } => instance,
        { ImplementationFactory: { } factory } => (IJobTenantAccessor)factory(services),
        { ImplementationType: { } type } => (IJobTenantAccessor)ActivatorUtilities.CreateInstance(services, type),
        _ => null,
    };
}

/// <summary>Serves the embedded single-page UI.</summary>
internal static class DashboardUi
{
    private const string ResourcePrefix = "ninjaqueue-ui/";
    private static readonly Assembly Assembly = typeof(DashboardUi).Assembly;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // Published path ("assets/x.js") -> resource name. Built on Windows, the names hold '' where the path has '/'.
    private static readonly Dictionary<string, string> Files = Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), name => name, StringComparer.Ordinal);

    public static IResult Serve(HttpContext http, string? path)
    {
        string file = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (file.StartsWith("api/", StringComparison.OrdinalIgnoreCase) || file.Contains("..", StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        if (!Files.ContainsKey(file) && !Path.HasExtension(file))
        {
            file = "index.html"; // client-side routes
        }

        Stream? stream = Files.TryGetValue(file, out string? resource) ? Assembly.GetManifestResourceStream(resource) : null;

        if (stream is null)
        {
            return file == "index.html"
                ? Results.Content("The NinjaQueue dashboard UI is not built. Run 'npm ci && npm run build' in src/NinjaQueue.Dashboard/ui.", "text/plain")
                : Results.NotFound();
        }

        IHeaderDictionary headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        headers.CacheControl = file == "index.html" ? "no-cache" : "public, max-age=31536000, immutable";
        string contentType = ContentTypes.TryGetContentType(file, out string? type) ? type : "application/octet-stream";
        return Results.Stream(stream, contentType);
    }
}