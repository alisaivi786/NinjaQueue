namespace NinjaQueue;

/// <summary>What every handler knows about the job it runs.</summary>
public abstract class JobContextBase
{
    /// <summary>Creates the context.</summary>
    protected JobContextBase(JobId jobId, string jobType, Guid? tenantId, int attempt, int maxAttempts, string? correlationId)
    {
        JobId = jobId;
        JobType = jobType;
        TenantId = tenantId;
        Attempt = attempt;
        MaxAttempts = maxAttempts;
        CorrelationId = correlationId;
    }

    /// <summary>The public job id.</summary>
    public JobId JobId { get; }

    /// <summary>The job code.</summary>
    public string JobType { get; }

    /// <summary>The tenant of a tenant-owned job.</summary>
    public Guid? TenantId { get; }

    /// <summary>The charged attempt number, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>The attempt budget.</summary>
    public int MaxAttempts { get; }

    /// <summary>The correlation id captured at enqueue.</summary>
    public string? CorrelationId { get; }

    /// <summary>
    /// Throws <see cref="PermanentJobException"/> unless <paramref name="payloadTenantId"/> is the job's tenant (§7.5): a
    /// payload can never make a job act on another tenant's data.
    /// </summary>
    public void EnsureTenant(Guid payloadTenantId)
    {
        if (TenantId != payloadTenantId)
        {
            throw new PermanentJobException($"Payload tenant {payloadTenantId} does not match the job's tenant {TenantId?.ToString() ?? "(none)"}.");
        }
    }
}

/// <summary>The context of one fenced step (§15.1).</summary>
public sealed class FencedJobContext : JobContextBase
{
    /// <summary>Creates the context.</summary>
    public FencedJobContext(JobId jobId, string jobType, Guid? tenantId, int attempt, int maxAttempts, string? correlationId, string? checkpoint)
        : base(jobId, jobType, tenantId, attempt, maxAttempts, correlationId)
        => Checkpoint = checkpoint;

    /// <summary>The checkpoint committed by the previous step, or null for the first step.</summary>
    public string? Checkpoint { get; }

    /// <summary>Progress reported by this step; stored with the step's finalization.</summary>
    public (int Processed, int? Total)? Progress { get; private set; }

    /// <summary>Records progress; it commits with this step.</summary>
    public void ReportProgress(int processed, int? total = null) => Progress = (processed, total);
}

/// <summary>The context of a standard handler's attempt.</summary>
public sealed class JobContext : JobContextBase
{
    private readonly Func<int, int?, CancellationToken, Task> _reportProgress;
    private readonly IJobSideEffectLedger? _ledger;

    /// <summary>Creates the context.</summary>
    public JobContext(
        JobId jobId, string jobType, Guid? tenantId, int attempt, int maxAttempts, string? correlationId,
        Func<int, int?, CancellationToken, Task> reportProgress)
        : this(jobId, jobType, tenantId, attempt, maxAttempts, correlationId, reportProgress, null)
    {
    }

    /// <summary>Creates the context of an <see cref="JobSideEffects.ExternalLedger"/> job, with its ledger.</summary>
    public JobContext(
        JobId jobId, string jobType, Guid? tenantId, int attempt, int maxAttempts, string? correlationId,
        Func<int, int?, CancellationToken, Task> reportProgress, IJobSideEffectLedger? ledger)
        : base(jobId, jobType, tenantId, attempt, maxAttempts, correlationId)
    {
        _reportProgress = reportProgress;
        _ledger = ledger;
    }

    /// <summary>Stores progress and renews the lease (lease-checked).</summary>
    public Task ReportProgressAsync(int processed, int? total, CancellationToken cancellationToken)
        => _reportProgress(processed, total, cancellationToken);

    /// <summary>
    /// The stable operation key of <paramref name="stepKey"/> for this job (<c>{jobId:N}:{stepKey}</c>): the same on every
    /// attempt, so pass it to providers that deduplicate by key (an <c>Idempotency-Key</c> header, a Message-ID, ...).
    /// </summary>
    public string OperationKey(string stepKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        return $"{JobId.Value:N}:{stepKey}";
    }

    /// <summary>
    /// Runs one external call through the durable operation ledger (§15.5), for <see cref="JobSideEffects.ExternalLedger"/>
    /// job types. A step completed by an earlier attempt is not issued again (its stored result is returned). A step an
    /// earlier attempt or a stale worker started without a known outcome is never issued blindly: <paramref name="reconcile"/>
    /// (when supplied) asks the provider, otherwise the <see cref="SideEffectAmbiguity"/> policy applies. The call is made
    /// outside any database transaction. Delivery is at least once; nothing here promises exactly-once external delivery.
    /// </summary>
    /// <param name="stepKey">A stable step name, unique within the job (at most 200 characters).</param>
    /// <param name="issue">Makes the call with the operation key and reports what happened. A
    /// <see cref="PermanentJobException"/> means "definitely not issued, never retry"; any other exception counts as an
    /// unknown outcome.</param>
    /// <param name="reconcile">Optional lookup by operation key: <see cref="SideEffectOutcome.Completed"/> when the provider
    /// has the operation, <see cref="SideEffectOutcome.NotIssued"/> when it does not.</param>
    /// <param name="options">Per-call policy overrides.</param>
    /// <param name="cancellationToken">The handler's token.</param>
    public Task<SideEffectResult> SideEffectAsync(
        string stepKey,
        Func<string, CancellationToken, Task<SideEffectOutcome>> issue,
        Func<string, CancellationToken, Task<SideEffectOutcome>>? reconcile = null,
        SideEffectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentNullException.ThrowIfNull(issue);
        return _ledger is null
            ? throw new InvalidOperationException($"'{JobType}' is not declared SideEffects = ExternalLedger, so it has no side-effect ledger.")
            : _ledger.RunAsync(stepKey, issue, reconcile, options, cancellationToken);
    }
}

/// <summary>The public id of a job (<c>background_jobs.guid_id</c>).</summary>
public readonly record struct JobId(Guid Value)
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}