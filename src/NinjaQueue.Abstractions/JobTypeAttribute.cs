namespace NinjaQueue;

/// <summary>How a job type's side effects are made safe under at-least-once delivery (§14.2, §15.1). Mandatory.</summary>
public enum JobSideEffects
{
    /// <summary>Not declared. The registry refuses to start.</summary>
    Unspecified = 0,

    /// <summary>All effects are writes to the job database, made through the engine-owned transaction. Exactly-once commit
    /// of those writes. The handler implements <see cref="IFencedJobHandler{TPayload}"/>.</summary>
    DatabaseFenced = 1,

    /// <summary>The work is naturally idempotent (upsert by business key, purge, ...). At least once.</summary>
    Idempotent = 2,

    /// <summary>External calls (HTTP, SMTP, ...) made through <see cref="JobContext.SideEffectAsync"/>, the durable operation
    /// ledger (§15.5). At least once per call, never issued blindly after an unknown outcome. The handler implements
    /// <see cref="IJobHandler{TPayload}"/> and declares <see cref="JobTypeAttribute.OnAmbiguous"/>.</summary>
    ExternalLedger = 3,
}

/// <summary>Whether a job belongs to a tenant (§14.5). Mandatory.</summary>
public enum JobTenancy
{
    /// <summary>Not declared. The registry refuses to start.</summary>
    Unspecified = 0,

    /// <summary>Every job carries a tenant id from the trusted request context; keys are scoped to it.</summary>
    TenantOwned = 1,

    /// <summary>A platform job with no tenant; keys use the system scope.</summary>
    System = 2,
}

/// <summary>
/// The declared contract of a job handler (§3.1, §15.12): a stable, versioned code plus side-effect mode and tenancy, which
/// are mandatory, and the queue, attempt budget and timeouts. The code is what is persisted; CLR type names never are.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobTypeAttribute(string code) : Attribute
{
    /// <summary>The stable job code, for example <c>Company.Onboarding.Provision.v1</c>. The version is part of the code.</summary>
    public string Code { get; } = code;

    /// <summary>How side effects are made safe. Mandatory.</summary>
    public JobSideEffects SideEffects { get; set; }

    /// <summary>Tenant-owned or system. Mandatory.</summary>
    public JobTenancy Tenancy { get; set; }

    /// <summary>The named queue (default <c>default</c>).</summary>
    public string Queue { get; set; } = "default";

    /// <summary>The attempt budget, 1–<see cref="JobLimits.MaxConfiguredAttempts"/>.</summary>
    public int MaxAttempts { get; set; } = JobLimits.DefaultMaxAttempts;

    /// <summary>For fenced jobs: the bound on one step's transaction (<c>hh:mm:ss</c>, default 30 s, at most 120 s).</summary>
    public string? StepTimeout { get; set; }

    /// <summary>For standard jobs: the handler timeout (<c>hh:mm:ss</c>, default 5 minutes).</summary>
    public string? Timeout { get; set; }

    /// <summary>False when a running attempt must never stop half-way: a cancel then applies after the attempt (§14.4).</summary>
    public bool Interruptible { get; set; } = true;

    /// <summary>
    /// Per-tenant fairness (§14.5): at most this many jobs of this type run at once per tenant (1–100; 0 = no cap). Enforced
    /// at claim with per-tenant slots and the running-concurrency-key unique index. A job of such a type cannot also carry
    /// <see cref="JobOptions.ConcurrencyKey"/>.
    /// </summary>
    public int MaxRunningPerTenant { get; set; }

    /// <summary>For <see cref="JobSideEffects.ExternalLedger"/> types: what to do when an external call's outcome is unknown
    /// (§15.5). Mandatory for those types; a call can override it.</summary>
    public SideEffectAmbiguity OnAmbiguous { get; set; }

    /// <summary>The admin API may show this type's payload to callers allowed to see payloads, with <see cref="SensitiveAttribute"/>
    /// properties masked (§16.8, §17.8). Default: payloads are never shown.</summary>
    public bool DashboardVisible { get; set; }
}