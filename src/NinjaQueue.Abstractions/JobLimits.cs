namespace NinjaQueue;

/// <summary>Limits shared by the schema, the registry and enqueue validation.</summary>
public static class JobLimits
{
    /// <summary>The persisted ceiling for <c>max_attempts</c> (schema CHECK). Admin Retry/Requeue may raise a job up to it.</summary>
    public const int MaxPersistedAttempts = 1000;

    /// <summary>The highest <c>MaxAttempts</c> a job type may configure (registry validation).</summary>
    public const int MaxConfiguredAttempts = 100;

    /// <summary>The default attempt budget.</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>Maximum length of a job type code.</summary>
    public const int MaxJobTypeLength = 200;

    /// <summary>Maximum length of a queue name.</summary>
    public const int MaxQueueLength = 50;

    /// <summary>Maximum length of an idempotency, unique or concurrency key.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The <c>tenant_scope</c> of system (non-tenant) jobs.</summary>
    public static readonly Guid SystemTenantScope = Guid.Empty;
}