namespace NinjaQueue;

/// <summary>
/// Reacts to a job of this type being dead-lettered, whichever path dead-lettered it (the executor's last attempt, a
/// permanent error, or lease-expiry recovery at the last attempt). The engine runs it once per dead-lettered job inside an
/// engine-owned transaction, together with the marker that records it ran, so its repository writes commit exactly once
/// (the same contract as a fenced step: do not commit, open a transaction, or call HTTP/SMTP here). Register it on the
/// job's handler class or as its own service; it is resolved from the job's DI scope.
/// </summary>
/// <typeparam name="TPayload">The job's payload record.</typeparam>
public interface IJobDeadLetterHandler<in TPayload>
{
    /// <summary>Records the business consequence of the dead letter, for example a <c>ProvisioningFailed</c> status.</summary>
    Task OnDeadLetteredAsync(TPayload payload, JobDeadLetterContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Runs instead of <see cref="OnDeadLetteredAsync"/> when the stored payload cannot be read as <typeparamref name="TPayload"/>
    /// (the job was dead-lettered as <c>InvalidPayload</c>, or retention already removed the payload). Same transaction and
    /// exactly-once contract. The default does nothing, so the dead letter is recorded as handled without a business
    /// consequence; override it to act on what the context still carries (for example <see cref="JobContextBase.TenantId"/>).
    /// </summary>
    Task OnPayloadUnreadableAsync(JobDeadLetterContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>What a dead-letter handler knows about the job that was dead-lettered.</summary>
public sealed class JobDeadLetterContext : JobContextBase
{
    /// <summary>Creates the context.</summary>
    public JobDeadLetterContext(
        JobId jobId, string jobType, Guid? tenantId, int attempt, int maxAttempts, string? correlationId, string? errorCode, string? errorMessage)
        : base(jobId, jobType, tenantId, attempt, maxAttempts, correlationId)
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>The engine's error code for the dead letter (for example <c>MaxAttemptsExceeded</c>).</summary>
    public string? ErrorCode { get; }

    /// <summary>The truncated, PII-free error message stored with the job.</summary>
    public string? ErrorMessage { get; }
}