using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using NinjaQueue.SqlServer;

namespace NinjaQueue.IntegrationTests.Dashboard;

/// <summary>
/// The dashboard end to end over HTTP on a real host and database: the default admin and the forced password change, CSRF,
/// lockout, sessions ending on a password change, admin-only endpoints, and tenant isolation (a tenant user never sees
/// another tenant's jobs, whatever tenant they ask for).
/// </summary>
[Collection(JobsDatabaseCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DashboardTests(JobsDatabaseFixture fixture)
{
    private const string NewAdminPassword = "Str0ng-admin-pass";
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

    public static TheoryData<DatabaseKind> Kinds => [DatabaseKind.Postgres, DatabaseKind.SqlServerRcsiOn];

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Default_admin_must_change_the_password_before_anything_else(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        using HttpClient client = host.CreateClient();

        (await client.GetAsync("api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(client, "admin", "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        HttpResponseMessage login = await Login(client, "admin", "admin");
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(login)).GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        HttpResponseMessage blocked = await client.GetAsync("api/jobs");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(blocked)).Should().Be("PasswordChangeRequired");

        HttpResponseMessage weak = await client.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "admin", newPassword = "admin" });
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(weak)).Should().Be("WeakPassword");

        HttpResponseMessage changed = await client.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "admin", newPassword = NewAdminPassword });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(changed)).GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();
        (await client.GetAsync("api/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpClient other = host.CreateClient();
        (await Login(other, "admin", "admin")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the initial password no longer counts");
        (await Login(other, "ADMIN", NewAdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "user names are case-insensitive");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task The_default_admin_exists_even_when_the_host_created_tenant_users_first(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        await host.SeedTenantUserAsync("early-user", TenantA);
        using HttpClient client = host.CreateClient();

        HttpResponseMessage login = await Login(client, "admin", "admin");
        login.StatusCode.Should().Be(HttpStatusCode.OK, "a tenant user created from code must not block the first admin");
        (await Data(login)).GetProperty("isAdmin").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task State_changes_need_the_csrf_header_and_failed_logins_lock_the_account(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        using HttpClient client = host.CreateClient();
        using HttpClient noCsrf = host.CreateClient(csrf: false);

        HttpResponseMessage forged = await noCsrf.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = "admin" });
        forged.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(forged)).Should().Be("CsrfHeaderMissing");

        await SignInAsAdminAsync(client);
        using HttpClient attacker = host.CreateClient();
        for (int i = 0; i < 5; i++)
        {
            (await Login(attacker, "admin", "guess-" + i)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        HttpResponseMessage locked = await Login(attacker, "admin", NewAdminPassword);
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "five failures lock the account, even for the right password");
        (await client.GetAsync("api/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a locked account's sessions stop working");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_password_change_ends_the_users_other_sessions(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        using HttpClient first = host.CreateClient();
        using HttpClient second = host.CreateClient();
        await SignInAsAdminAsync(first);
        (await Login(second, "admin", NewAdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await second.PostAsJsonAsync("api/auth/change-password", new { currentPassword = NewAdminPassword, newPassword = "An0ther-pass-word" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await first.GetAsync("api/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await second.GetAsync("api/jobs")).StatusCode.Should().Be(HttpStatusCode.OK, "the session that changed it gets a fresh cookie");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_tenant_user_sees_only_their_tenants_jobs(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        JobId jobA = await host.EnqueueAsync(TenantA);
        JobId jobB = await host.EnqueueAsync(TenantB);

        using HttpClient admin = host.CreateClient();
        await SignInAsAdminAsync(admin);
        (await admin.PostAsJsonAsync("api/tenants", new { tenantId = TenantA, name = "Tenant A" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("api/tenants", new { tenantId = TenantB, name = "Tenant B" })).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage created = await admin.PostAsJsonAsync("api/users", new { userName = "alice", password = "alice-initial-1", isAdmin = false, tenantIds = new[] { TenantA } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        (await JobIds(admin, "api/jobs")).Should().Contain([jobA.Value, jobB.Value], "an admin sees every tenant");
        (await JobIds(admin, $"api/jobs?tenantId={TenantB}")).Should().Equal([jobB.Value], "the admin's tenant filter");

        using HttpClient alice = host.CreateClient();
        (await Login(alice, "alice", "alice-initial-1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await alice.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "alice-initial-1", newPassword = "alice-own-pass-2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await JobIds(alice, "api/jobs")).Should().Equal([jobA.Value]);
        (await JobIds(alice, $"api/jobs?tenantId={TenantB}")).Should().BeEmpty("a filter outside the user's tenants shows nothing");
        (await alice.GetAsync($"api/jobs/{jobB.Value}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "another tenant's job looks missing, never forbidden");
        (await alice.GetAsync($"api/jobs/{jobA.Value}")).StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpRequestMessage asB = new(HttpMethod.Get, "api/jobs");
        asB.Headers.Add("X-NinjaQueue-Tenant", TenantB.ToString());
        (await Items(await alice.SendAsync(asB))).Should().BeEmpty("the tenant switcher cannot reach a tenant the user does not have");

        HttpResponseMessage cancelB = await alice.PostAsJsonAsync($"api/jobs/{jobB.Value}/cancel", new { reason = "not mine" });
        cancelB.StatusCode.Should().Be(HttpStatusCode.NotFound);

        HttpResponseMessage adminOnly = await alice.GetAsync("api/users");
        adminOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(adminOnly)).Should().Be("AdminOnly");
        (await alice.GetAsync("api/jobs/workers")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        JsonElement me = await Data(await alice.GetAsync("api/auth/me"));
        me.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Equal(["Tenant A"]);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Job_actions_need_a_reason_and_are_recorded(DatabaseKind kind)
    {
        await using DashboardHost host = await DashboardHost.StartAsync(await fixture.CreateDatabaseAsync(kind));
        JobId job = await host.EnqueueAsync(TenantA);
        using HttpClient admin = host.CreateClient();
        await SignInAsAdminAsync(admin);

        HttpResponseMessage noReason = await admin.PostAsJsonAsync($"api/jobs/{job.Value}/cancel", new { reason = " " });
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(noReason)).Should().Be("ReasonRequired");

        HttpResponseMessage cancelled = await admin.PostAsJsonAsync($"api/jobs/{job.Value}/cancel", new { reason = "customer asked" });
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Data(cancelled)).GetProperty("result").GetString().Should().Be("Cancelled");

        JsonElement detail = await Data(await admin.GetAsync($"api/jobs/{job.Value}"));
        detail.GetProperty("job").GetProperty("status").GetString().Should().Be("Cancelled");
        detail.GetProperty("events").EnumerateArray().Should().Contain(e => e.GetProperty("reason").GetString() == "customer asked");

        HttpResponseMessage again = await admin.PostAsJsonAsync($"api/jobs/{job.Value}/cancel", new { reason = "twice" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);

        HttpResponseMessage page = await admin.GetAsync(string.Empty);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? csp).Should().BeTrue();
        csp!.Single().Should().Contain("frame-ancestors 'none'");
    }

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        (await Login(client, "admin", "admin")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("api/auth/change-password", new { currentPassword = "admin", newPassword = NewAdminPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string userName, string password)
        => client.PostAsJsonAsync("api/auth/login", new { userName, password });

    private static async Task<JsonElement> Data(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString();

    private static async Task<List<Guid>> Items(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await Data(response)).GetProperty("items").EnumerateArray().Select(j => j.GetProperty("id").GetGuid())];
    }

    private static async Task<List<Guid>> JobIds(HttpClient client, string url) => await Items(await client.GetAsync(url));
}

/// <summary>A tenant-owned job type for the dashboard tests (workers are off: jobs stay queued).</summary>
public sealed record DashboardProbe(int Number);

[JobType("Test.Dashboard.Probe.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, DashboardVisible = true)]
public sealed class DashboardProbeHandler : IJobHandler<DashboardProbe>
{
    public Task<JobResult> HandleAsync(DashboardProbe payload, JobContext context, CancellationToken cancellationToken)
        => Task.FromResult(JobResult.Success());
}

/// <summary>A real Kestrel host on a free port with NinjaQueue (standalone mode) and the dashboard at /jobs.</summary>
internal sealed class DashboardHost(WebApplication app, Uri baseAddress) : IAsyncDisposable
{
    public static async Task<DashboardHost> StartAsync(TestDatabase db)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        NinjaQueueBuilder jobs = builder.Services.AddNinjaQueue(configure: o => o.Worker.Enabled = false);
        _ = db.IsPostgres ? jobs.UsePostgreSql(db.ConnectionString) : jobs.UseSqlServer(db.ConnectionString);
        jobs.AddJobHandler<DashboardProbeHandler>();
        builder.Services.AddNinjaQueueDashboard();

        WebApplication app = builder.Build();
        app.MapNinjaQueueDashboard("/jobs");
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new DashboardHost(app, new Uri(address.TrimEnd('/') + "/jobs/"));
    }

    public HttpClient CreateClient(bool csrf = true)
    {
#pragma warning disable CA2000 // the client owns and disposes the handler
        HttpClient client = new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }, disposeHandler: true) { BaseAddress = baseAddress };
#pragma warning restore CA2000
        if (csrf)
        {
            client.DefaultRequestHeaders.Add("X-NinjaQueue-Csrf", "1");
        }

        return client;
    }

    public async Task<JobId> EnqueueAsync(Guid tenantId)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobEnqueuer>().EnqueueAsync(
            new DashboardProbe(1), new JobOptions { TenantId = tenantId, RunAt = DateTimeOffset.UtcNow.AddHours(1) });
    }

    public async Task SeedTenantUserAsync(string userName, Guid tenantId)
    {
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        NinjaQueue.Dashboard.NinjaQueueDashboardUsers users = scope.ServiceProvider.GetRequiredService<NinjaQueue.Dashboard.NinjaQueueDashboardUsers>();
        await users.UpsertTenantAsync(tenantId, "Early tenant");
        await users.EnsureUserAsync(userName, "early-pass-123", isAdmin: false, [tenantId]);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}