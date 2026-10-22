using System.Collections.Concurrent;
using NinjaQueue.Admin;
using NinjaQueue.IntegrationTests.Phase4;
using NinjaQueue.Storage;

namespace NinjaQueue.IntegrationTests.Phase7b;

// Hosts, handlers and helpers for the Phase 7b (Release 2, kit part) suites. Every host is a Phase4Host (so the Phase 4
// handlers and verification helpers are available) plus the handlers below and a mutable tenant accessor and audit sink.

/// <summary>What the Phase 7b handlers do and what they observed (one per host).</summary>
public sealed class P7bPlan
{
    private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

    /// <summary>The highest number of handlers seen running at once per tracking key.</summary>
    public ConcurrentDictionary<string, int> MaxRunning { get; } = new(StringComparer.Ordinal);

    /// <summary>Behaviour of <see cref="P7bStepHandler"/> / <see cref="P7bNonInterruptibleHandler"/> (null = succeed).</summary>
    public Func<P7bPayload, JobContext, CancellationToken, Task<JobResult>>? Step { get; set; }

    /// <summary>The external call of <see cref="P7bExternalHandler"/> (null = completed).</summary>
    public Func<P7bExternalPayload, JobContext, string, CancellationToken, Task<SideEffectOutcome>>? Issue { get; set; }

    /// <summary>The optional reconcile of <see cref="P7bExternalHandler"/>.</summary>
    public Func<string, CancellationToken, Task<SideEffectOutcome>>? Reconcile { get; set; }

    /// <summary>Per-call options of <see cref="P7bExternalHandler"/>.</summary>
    public SideEffectOptions? SideEffectOptions { get; set; }

    /// <summary>Thrown after the side effect returns, on these attempts (simulates a failure after the call).</summary>
    public HashSet<int> FailAfterSideEffectOnAttempts { get; } = [];

    /// <summary>Every call the simulated provider accepted (operation keys).</summary>
    public ConcurrentQueue<string> ProviderReceipts { get; } = new();

    /// <summary>Results returned by SideEffectAsync.</summary>
    public ConcurrentQueue<SideEffectResult> Results { get; } = new();

    public Task Started(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    internal void MarkStarted(string name) => _started.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Counts a running handler under <paramref name="key"/> until the returned scope is disposed.</summary>
    internal IDisposable Track(string key)
    {
        int now = _running.AddOrUpdate(key, 1, (_, n) => n + 1);
        MaxRunning.AddOrUpdate(key, now, (_, max) => Math.Max(max, now));
        return new Release(() => _running.AddOrUpdate(key, 0, (_, n) => n - 1));
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

public sealed record P7bPayload(string Name, int DelayMs = 0);

public sealed record P7bTenantPayload(Guid TenantId, string Name, int DelayMs = 0, string? Track = null);

/// <summary>A standard system job driven by <see cref="P7bPlan.Step"/>.</summary>
[JobType("Test.P7b.Step.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 3, Timeout = "00:01:00")]
public sealed class P7bStepHandler(P7bPlan plan) : IJobHandler<P7bPayload>
{
    public async Task<JobResult> HandleAsync(P7bPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        plan.MarkStarted(payload.Name);
        if (plan.Step is { } step)
        {
            return await step(payload, context, cancellationToken);
        }

        if (payload.DelayMs > 0)
        {
            await Task.Delay(payload.DelayMs, cancellationToken);
        }

        return JobResult.Success();
    }
}

public sealed record P7bNonInterruptiblePayload(string Name);

/// <summary>Like <see cref="P7bStepHandler"/>, but a running attempt is never interrupted (§14.4 step 6).</summary>
[JobType("Test.P7b.NonInterruptible.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.System, MaxAttempts = 3, Timeout = "00:01:00",
    Interruptible = false)]
public sealed class P7bNonInterruptibleHandler(P7bPlan plan) : IJobHandler<P7bNonInterruptiblePayload>
{
    public Task<JobResult> HandleAsync(P7bNonInterruptiblePayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        plan.MarkStarted(payload.Name);
        return plan.Step?.Invoke(new P7bPayload(payload.Name), context, cancellationToken) ?? Task.FromResult(JobResult.Success());
    }
}

/// <summary>A tenant-owned standard job that tracks how many run at once per <see cref="P7bTenantPayload.Track"/> key.</summary>
[JobType("Test.P7b.Keyed.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, MaxAttempts = 3, Timeout = "00:01:00")]
public sealed class P7bKeyedHandler(P7bPlan plan) : IJobHandler<P7bTenantPayload>
{
    public async Task<JobResult> HandleAsync(P7bTenantPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureTenant(payload.TenantId);
        using (plan.Track(payload.Track ?? payload.TenantId.ToString()))
        {
            await Task.Delay(payload.DelayMs, cancellationToken);
        }

        return JobResult.Success();
    }
}

public sealed record P7bFairPayload(Guid TenantId, int DelayMs);

/// <summary>A tenant-owned job capped at two running per tenant (§14.5 MaxRunningPerTenant).</summary>
[JobType("Test.P7b.Fair.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, MaxAttempts = 3, Timeout = "00:01:00", MaxRunningPerTenant = 2)]
public sealed class P7bFairHandler(P7bPlan plan) : IJobHandler<P7bFairPayload>
{
    public async Task<JobResult> HandleAsync(P7bFairPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using (plan.Track("fair:" + payload.TenantId))
        using (plan.Track("fair:all"))
        {
            await Task.Delay(payload.DelayMs, cancellationToken);
        }

        return JobResult.Success();
    }
}

public sealed record P7bExternalPayload(string Name);

/// <summary>An external-ledger system job: one step "send" through the ledger, driven by the plan.</summary>
[JobType("Test.P7b.External.v1", SideEffects = JobSideEffects.ExternalLedger, Tenancy = JobTenancy.System, MaxAttempts = 4, Timeout = "00:01:00",
    OnAmbiguous = SideEffectAmbiguity.Retry)]
public sealed class P7bExternalHandler(P7bPlan plan) : IJobHandler<P7bExternalPayload>
{
    public async Task<JobResult> HandleAsync(P7bExternalPayload payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        plan.MarkStarted($"{payload.Name}#{context.Attempt}");
        SideEffectResult result = await context.SideEffectAsync("send",
            (key, ct) => plan.Issue?.Invoke(payload, context, key, ct) ?? Accept(key),
            plan.Reconcile, plan.SideEffectOptions, cancellationToken);
        plan.Results.Enqueue(result);
        if (plan.FailAfterSideEffectOnAttempts.Contains(context.Attempt))
        {
            throw new InvalidOperationException($"Injected failure after the side effect on attempt {context.Attempt}.");
        }

        return JobResult.Success();
    }

    private Task<SideEffectOutcome> Accept(string key)
    {
        plan.ProviderReceipts.Enqueue(key);
        return Task.FromResult(SideEffectOutcome.Completed("msg-" + key));
    }
}

public sealed record P7bVisiblePayload(Guid TenantId, string Subject, [property: Sensitive] string Recipient, P7bVisibleInner Inner);

public sealed record P7bVisibleInner(string Note, [property: Sensitive] string Secret);

/// <summary>A tenant-owned type whose (redacted) payload the admin API may show.</summary>
[JobType("Test.P7b.Visible.v1", SideEffects = JobSideEffects.Idempotent, Tenancy = JobTenancy.TenantOwned, MaxAttempts = 2, DashboardVisible = true)]
public sealed class P7bVisibleHandler : IJobHandler<P7bVisiblePayload>
{
    public Task<JobResult> HandleAsync(P7bVisiblePayload payload, JobContext context, CancellationToken cancellationToken) => Task.FromResult(JobResult.Success());
}

/// <summary>A settable <see cref="IJobTenantAccessor"/> (null = all tenants).</summary>
public sealed class TestTenants : IJobTenantAccessor
{
    public IReadOnlyCollection<Guid>? AllowedTenantScopes { get; set; }
}

/// <summary>Records <see cref="IJobAuditSink"/> calls.</summary>
public sealed class RecordingAuditSink : IJobAuditSink
{
    public ConcurrentQueue<JobAuditEntry> Entries { get; } = new();

    public Task RecordAsync(JobAuditEntry entry, CancellationToken cancellationToken)
    {
        Entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

internal static class P7b
{
    public const string Step = "Test.P7b.Step.v1";
    public const string NonInterruptible = "Test.P7b.NonInterruptible.v1";
    public const string Keyed = "Test.P7b.Keyed.v1";
    public const string Fair = "Test.P7b.Fair.v1";
    public const string External = "Test.P7b.External.v1";
    public const string Visible = "Test.P7b.Visible.v1";
    public const string P4Standard = "Test.P4.Standard.v1";

    public static readonly Guid Actor = Guid.Parse("7b7b7b7b-0000-0000-0000-0000000000ad");
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-7b00-0000-0000-0000000000a1");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-7b00-0000-0000-0000000000b2");

    /// <summary>A Phase 4 host on a fresh database with the Phase 7b handlers, tenant accessor and audit sink.</summary>
    public static async Task<Phase4Host> HostAsync(JobsDatabaseFixture fixture, DatabaseKind kind, Action<NinjaQueueOptions>? configure = null)
        => On(await Phase4Host.CreateDatabaseAsync(fixture, kind), configure);

    /// <summary>Another host (worker) on the same database.</summary>
    public static Phase4Host On(TestDatabase db, Action<NinjaQueueOptions>? configure = null)
        => Phase4Host.Build(db, configure, b =>
        {
            b.AddJobHandler<P7bStepHandler>()
                .AddJobHandler<P7bNonInterruptibleHandler>()
                .AddJobHandler<P7bKeyedHandler>()
                .AddJobHandler<P7bFairHandler>()
                .AddJobHandler<P7bExternalHandler>()
                .AddJobHandler<P7bVisibleHandler>();
            b.Services.AddSingleton<P7bPlan>();
            b.Services.AddSingleton<TestTenants>();
            b.Services.AddSingleton<IJobTenantAccessor>(sp => sp.GetRequiredService<TestTenants>());
            b.Services.AddSingleton<RecordingAuditSink>();
            b.Services.AddSingleton<IJobAuditSink>(sp => sp.GetRequiredService<RecordingAuditSink>());
            b.Services.AddSingleton<CapturingLoggerProvider>();
            b.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(sp => sp.GetRequiredService<CapturingLoggerProvider>());
        });

    public static P7bPlan Plan(this Phase4Host host) => host.Get<P7bPlan>();

    public static Task<T> AdminAsync<T>(this Phase4Host host, Func<IJobAdminService, Task<T>> work)
        => host.InScopeAsync(sp => work(sp.GetRequiredService<IJobAdminService>()));

    public static Task<CancelResult> CancelAsync(this Phase4Host host, JobId id, string reason = "test cancel")
        => host.AdminAsync(a => a.CancelAsync(id, Actor, reason));

    /// <summary>Continuation-specific invariants (§14.8 #7) on top of <see cref="JobInvariants"/>.</summary>
    public static async Task AssertInvariantsAsync(TestDatabase db, params string[] skip)
    {
        await JobInvariants.AssertHoldAsync(db, skip);
        (await db.ScalarAsync<int>("""
            SELECT count(*) FROM ninjaqueue.background_jobs c JOIN ninjaqueue.background_jobs p ON p.id = c.parent_job_id WHERE c.status = 3 AND p.status >= 10
            """)).Should().Be(0, "no Awaiting child has a terminal parent (§14.8 #7)");
        (await db.ScalarAsync<int>("SELECT count(*) FROM ninjaqueue.job_queue q JOIN ninjaqueue.background_jobs b ON b.id = q.job_id WHERE b.status = 3"))
            .Should().Be(0, "an Awaiting job has no queue row");
        (await db.ScalarAsync<int>("""
            SELECT count(*) FROM (SELECT tenant_scope, concurrency_key FROM ninjaqueue.job_queue WHERE lease_token IS NOT NULL AND concurrency_key IS NOT NULL
             GROUP BY tenant_scope, concurrency_key HAVING count(*) > 1) x
            """)).Should().Be(0, "at most one running job per concurrency key");
    }

    public static async Task<int> EventCountAsync(this Phase4Host host, JobId id, string code)
        => (await host.EventsAsync(id)).Count(e => e == code);

    public static Task<int> ExpireLeaseAsync(this Phase4Host host, long dbId) => host.Database.ExpireLeaseAsync(dbId);

    /// <summary>The ledger row of step "send": (status, issue_count, owner token).</summary>
    public static async Task<(SideEffectStatus Status, int IssueCount, Guid Owner)?> LedgerAsync(this Phase4Host host, JobId id)
    {
        List<object?[]> rows = await host.Database.QueryAsync($"""
            SELECT s.status, s.issue_count, s.owner_lease_token FROM ninjaqueue.job_side_effects s JOIN ninjaqueue.background_jobs b ON b.id = s.job_id
             WHERE b.guid_id = '{id.Value}' AND s.step_key = 'send'
            """);
        return rows.Count == 0 ? null : ((SideEffectStatus)Convert.ToInt16(rows[0][0], System.Globalization.CultureInfo.InvariantCulture), Phase4Host.Int(rows[0][1]), (Guid)rows[0][2]!);
    }

    /// <summary>Polls until <paramref name="condition"/> holds, or fails the test with the job's state from the database.</summary>
    public static async Task WaitForJobAsync(TestDatabase db, JobId id, Func<Task<bool>> condition, int seconds = 30, string because = "")
    {
        if (await JobProbes.EventuallyAsync(condition, TimeSpan.FromSeconds(seconds)))
        {
            return;
        }

        (bool Leased, bool Expired, int AttemptsLeft)? queue = await db.QueueStateAsync(id);
        string state = $"status {await db.StatusOfAsync(id)}, queue {queue?.ToString() ?? "none"}, events [{string.Join(", ", await db.EventsOfAsync(id))}], "
            + $"attempts [{string.Join(", ", await db.AttemptOutcomesAsync(id))}]";
        false.Should().BeTrue($"{because} — {state}");
    }

    /// <summary>Polls until <paramref name="condition"/> holds or fails the test after <paramref name="seconds"/>.</summary>
    public static async Task WaitForAsync(Func<Task<bool>> condition, int seconds = 30, string because = "")
        => (await JobProbes.EventuallyAsync(condition, TimeSpan.FromSeconds(seconds))).Should().BeTrue(because);
}
/// <summary>Captures warning-and-above log lines (with exceptions) so a failing test can show what the engine logged.</summary>
public sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                owner.Lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }
}