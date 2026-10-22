using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NinjaQueue.Registry;

/// <summary>One registered job type: its declared contract and the handler that runs it.</summary>
public sealed record JobTypeRegistration(
    string Code,
    Type PayloadType,
    Type HandlerType,
    JobSideEffects SideEffects,
    JobTenancy Tenancy,
    string Queue,
    int MaxAttempts,
    TimeSpan StepTimeout,
    TimeSpan Timeout,
    bool Interruptible)
{
    /// <summary>The per-tenant running cap (0 = none, §14.5).</summary>
    public int MaxRunningPerTenant { get; init; }

    /// <summary>The ambiguity policy of an <see cref="JobSideEffects.ExternalLedger"/> type (§15.5).</summary>
    public SideEffectAmbiguity OnAmbiguous { get; init; }

    /// <summary>Whether the admin API may show (redacted) payloads of this type (§17.8).</summary>
    public bool DashboardVisible { get; init; }

    /// <summary>True for <see cref="JobSideEffects.ExternalLedger"/> job types.</summary>
    public bool UsesLedger => SideEffects == JobSideEffects.ExternalLedger;

    /// <summary>True for <see cref="JobSideEffects.DatabaseFenced"/> job types.</summary>
    public bool IsFenced => SideEffects == JobSideEffects.DatabaseFenced;

    /// <summary>The class implementing <see cref="IJobDeadLetterHandler{TPayload}"/> for this type, or null (plan §18 F7).</summary>
    public Type? DeadLetterHandlerType { get; init; }
}

/// <summary>The host start was refused because job registrations are invalid.</summary>
public sealed class JobRegistryException : InvalidOperationException
{
    /// <summary>Creates the exception from every problem found.</summary>
    public JobRegistryException(IReadOnlyList<string> errors)
        : base("NinjaQueue refused to start:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
        => Errors = errors;

    /// <summary>Creates the exception.</summary>
    public JobRegistryException()
        : this([])
    {
    }

    /// <summary>Creates the exception.</summary>
    public JobRegistryException(string message)
        : base(message) => Errors = [message];

    /// <summary>Creates the exception.</summary>
    public JobRegistryException(string message, Exception innerException)
        : base(message, innerException) => Errors = [message];

    /// <summary>Every validation error.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// The allow-list of job types: code ⇄ payload type ⇄ handler, built once at startup, validated and then frozen (§3.1,
/// §4.1, §14.3). Database values are only ever looked up here; no CLR type is ever resolved from a stored string.
/// </summary>
public sealed partial class JobHandlerRegistry
{
    /// <summary>The ceiling for a fenced step's transaction (§15.1).</summary>
    public static readonly TimeSpan MaxStepTimeout = TimeSpan.FromSeconds(120);

    private readonly Dictionary<string, JobTypeRegistration> _byCode;
    private readonly Dictionary<Type, JobTypeRegistration> _byPayload;

    private JobHandlerRegistry(Dictionary<string, JobTypeRegistration> byCode, Dictionary<Type, JobTypeRegistration> byPayload)
    {
        _byCode = byCode;
        _byPayload = byPayload;
        Codes = [.. byCode.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>Every registered code, sorted.</summary>
    public IReadOnlyList<string> Codes { get; }

    /// <summary>Every registration.</summary>
    public IEnumerable<JobTypeRegistration> All => _byCode.Values;

    /// <summary>Finds a job type by code.</summary>
    public bool TryGetByCode(string code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JobTypeRegistration? registration)
        => _byCode.TryGetValue(code, out registration);

    /// <summary>Finds the job type of a payload type.</summary>
    public bool TryGetByPayload(Type payloadType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JobTypeRegistration? registration)
        => _byPayload.TryGetValue(payloadType, out registration);

    /// <summary>The codes registered on <paramref name="queue"/>.</summary>
    public IReadOnlyList<string> CodesForQueue(string queue)
        => [.. _byCode.Values.Where(r => r.Queue == queue).Select(r => r.Code).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Builds the registry from handler types and validates every declared contract. Any problem refuses the host start
    /// with all problems listed: a missing <see cref="JobTypeAttribute"/>, side-effect mode or tenancy, a malformed or
    /// duplicate code, a handler interface that does not match the side-effect mode, an out-of-range attempt budget or
    /// timeout, a per-tenant cap out of range, or an external-ledger type without an ambiguity policy.
    /// </summary>
    public static JobHandlerRegistry Build(IEnumerable<Type> handlerTypes, NinjaQueueOptions options)
        => Build(handlerTypes, [], options);

    /// <summary>
    /// Builds the registry as <see cref="Build(IEnumerable{Type}, NinjaQueueOptions)"/> does, and attaches dead-letter handlers:
    /// a job handler class that also implements <see cref="IJobDeadLetterHandler{TPayload}"/> for its own payload, and each
    /// separately registered <paramref name="deadLetterHandlerTypes"/> class. A dead-letter handler for an unregistered payload,
    /// for another type's payload on a job handler class, or a second one for the same payload refuses the host start.
    /// </summary>
    public static JobHandlerRegistry Build(IEnumerable<Type> handlerTypes, IEnumerable<Type> deadLetterHandlerTypes, NinjaQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(handlerTypes);
        ArgumentNullException.ThrowIfNull(deadLetterHandlerTypes);
        ArgumentNullException.ThrowIfNull(options);

        List<string> errors = [];
        Dictionary<string, JobTypeRegistration> byCode = [with(StringComparer.Ordinal)];
        Dictionary<Type, JobTypeRegistration> byPayload = [];

        foreach (Type handlerType in handlerTypes.Distinct())
        {
            JobTypeRegistration? registration = Describe(handlerType, options, errors);
            if (registration is null)
            {
                continue;
            }

            if (!byCode.TryAdd(registration.Code, registration))
            {
                errors.Add($"Job code '{registration.Code}' is registered by both {byCode[registration.Code].HandlerType.FullName} and {handlerType.FullName}.");
                continue;
            }

            if (!byPayload.TryAdd(registration.PayloadType, registration))
            {
                errors.Add($"Payload type {registration.PayloadType.FullName} is used by both '{byPayload[registration.PayloadType].Code}' and '{registration.Code}'. Each job code needs its own payload type.");
            }
        }

        AttachDeadLetterHandlers(byCode, byPayload, deadLetterHandlerTypes, errors);
        return errors.Count > 0 ? throw new JobRegistryException(errors) : new JobHandlerRegistry(byCode, byPayload);
    }

    private static void AttachDeadLetterHandlers(
        Dictionary<string, JobTypeRegistration> byCode, Dictionary<Type, JobTypeRegistration> byPayload, IEnumerable<Type> separate, List<string> errors)
    {
        void Attach(JobTypeRegistration registration, Type handler)
        {
            JobTypeRegistration updated = registration with { DeadLetterHandlerType = handler };
            byCode[registration.Code] = updated;
            byPayload[registration.PayloadType] = updated;
        }

        // On the job's own handler class: only for its own payload.
        foreach (JobTypeRegistration registration in byCode.Values.ToList())
        {
            Type[] payloads = HandlerPayloads(registration.HandlerType, typeof(IJobDeadLetterHandler<>));
            if (payloads.Length == 0)
            {
                continue;
            }

            if (payloads.Length != 1 || payloads[0] != registration.PayloadType)
            {
                errors.Add($"{registration.HandlerType.FullName} ('{registration.Code}'): a job handler class may implement IJobDeadLetterHandler<T> only for its own payload {registration.PayloadType.Name}.");
                continue;
            }

            Attach(registration, registration.HandlerType);
        }

        // Registered as their own services.
        foreach (Type handler in separate.Distinct())
        {
            string name = handler.FullName ?? handler.Name;
            Type[] payloads = handler.IsAbstract || handler.IsInterface || handler.IsGenericTypeDefinition ? [] : HandlerPayloads(handler, typeof(IJobDeadLetterHandler<>));
            if (payloads.Length == 0)
            {
                errors.Add($"{name} is registered as a dead-letter handler but is not a concrete class implementing IJobDeadLetterHandler<T>.");
                continue;
            }

            foreach (Type payload in payloads)
            {
                if (!byPayload.TryGetValue(payload, out JobTypeRegistration? registration))
                {
                    errors.Add($"{name}: IJobDeadLetterHandler<{payload.Name}> has no registered job type for that payload.");
                }
                else if (registration.DeadLetterHandlerType is { } existing && existing != handler)
                {
                    errors.Add($"'{registration.Code}' has two dead-letter handlers: {existing.FullName} and {name}.");
                }
                else
                {
                    Attach(registration, handler);
                }
            }
        }
    }

    private static JobTypeRegistration? Describe(Type handlerType, NinjaQueueOptions options, List<string> errors)
    {
        string name = handlerType.FullName ?? handlerType.Name;
        if (handlerType.IsAbstract || handlerType.IsInterface || handlerType.IsGenericTypeDefinition)
        {
            errors.Add($"{name} must be a concrete, non-generic class.");
            return null;
        }

        JobTypeAttribute? attribute = handlerType.GetCustomAttribute<JobTypeAttribute>();
        if (attribute is null)
        {
            errors.Add($"{name} has no [JobType] attribute: every handler declares its code, side effects and tenancy.");
            return null;
        }

        int before = errors.Count;
        string code = attribute.Code ?? "";
        if (code.Length > JobLimits.MaxJobTypeLength || !CodePattern().IsMatch(code))
        {
            errors.Add($"{name}: job code '{code}' must look like 'Area.Action.v1' (segments of letters and digits, ending in a version) and be at most {JobLimits.MaxJobTypeLength} characters.");
        }

        Type[] fenced = HandlerPayloads(handlerType, typeof(IFencedJobHandler<>));
        Type[] standard = HandlerPayloads(handlerType, typeof(IJobHandler<>));
        ValidateSideEffects(attribute, fenced, standard, name, code, errors);

        if (attribute.Tenancy == JobTenancy.Unspecified)
        {
            errors.Add($"{name} ('{code}'): Tenancy is not declared (TenantOwned or System).");
        }

        if (attribute.MaxAttempts is < 1 or > JobLimits.MaxConfiguredAttempts)
        {
            errors.Add($"{name} ('{code}'): MaxAttempts must be between 1 and {JobLimits.MaxConfiguredAttempts}.");
        }

        if (!QueuePattern().IsMatch(attribute.Queue ?? ""))
        {
            errors.Add($"{name} ('{code}'): queue '{attribute.Queue}' must be lowercase letters, digits, '-' or '_' (at most {JobLimits.MaxQueueLength}).");
        }

        TimeSpan stepTimeout = ParseTimeout(attribute.StepTimeout, TimeSpan.FromSeconds(30), "StepTimeout", name, code, errors);
        if (attribute.SideEffects == JobSideEffects.DatabaseFenced
            && (stepTimeout > MaxStepTimeout || stepTimeout >= options.Worker.LeaseDuration))
        {
            errors.Add($"{name} ('{code}'): StepTimeout {stepTimeout} must be at most {MaxStepTimeout} and below the lease duration {options.Worker.LeaseDuration}.");
        }

        TimeSpan timeout = ParseTimeout(attribute.Timeout, TimeSpan.FromMinutes(5), "Timeout", name, code, errors);

        ValidateMaxRunningPerTenant(attribute, name, code, errors);

        if (errors.Count > before)
        {
            return null;
        }

        Type payload = attribute.SideEffects == JobSideEffects.DatabaseFenced ? fenced[0] : standard[0];
        return new JobTypeRegistration(code, payload, handlerType, attribute.SideEffects, attribute.Tenancy, attribute.Queue!,
            attribute.MaxAttempts, stepTimeout, timeout, attribute.Interruptible)
        {
            MaxRunningPerTenant = attribute.MaxRunningPerTenant,
            OnAmbiguous = attribute.OnAmbiguous,
            DashboardVisible = attribute.DashboardVisible,
        };
    }

    private static void ValidateSideEffects(JobTypeAttribute attribute, Type[] fenced, Type[] standard, string name, string code, List<string> errors)
    {
        switch (attribute.SideEffects)
        {
            case JobSideEffects.Unspecified:
                errors.Add($"{name} ('{code}'): SideEffects is not declared (DatabaseFenced, Idempotent or ExternalLedger).");
                break;
            case JobSideEffects.ExternalLedger when standard.Length != 1 || fenced.Length != 0:
                errors.Add($"{name} ('{code}'): an ExternalLedger handler implements exactly one IJobHandler<T> and no IFencedJobHandler<T>.");
                break;
            case JobSideEffects.ExternalLedger when attribute.OnAmbiguous is SideEffectAmbiguity.Unspecified || !Enum.IsDefined(attribute.OnAmbiguous):
                errors.Add($"{name} ('{code}'): an ExternalLedger handler declares OnAmbiguous (Retry, MarkUnknown or DeadLetter).");
                break;
            case JobSideEffects.DatabaseFenced when fenced.Length != 1 || standard.Length != 0:
                errors.Add($"{name} ('{code}'): a DatabaseFenced handler implements exactly one IFencedJobHandler<T> and no IJobHandler<T>.");
                break;
            case JobSideEffects.Idempotent when standard.Length != 1 || fenced.Length != 0:
                errors.Add($"{name} ('{code}'): an Idempotent handler implements exactly one IJobHandler<T> and no IFencedJobHandler<T>.");
                break;
        }
    }

    private static void ValidateMaxRunningPerTenant(JobTypeAttribute attribute, string name, string code, List<string> errors)
    {
        if (attribute.MaxRunningPerTenant is < 0 or > Enqueue.ConcurrencyKeys.MaxSlots)
        {
            errors.Add($"{name} ('{code}'): MaxRunningPerTenant must be between 0 (no cap) and {Enqueue.ConcurrencyKeys.MaxSlots}.");
        }
        else if (attribute.MaxRunningPerTenant > 0 && Enqueue.ConcurrencyKeys.MaxSlotKeyLength(code.Length) > JobLimits.MaxKeyLength)
        {
            errors.Add($"{name} ('{code}'): the code is too long for MaxRunningPerTenant slot keys (at most {JobLimits.MaxKeyLength - Enqueue.ConcurrencyKeys.MaxSlotKeyLength(0)} characters).");
        }
    }

    private static TimeSpan ParseTimeout(string? text, TimeSpan fallback, string property, string name, string code, List<string> errors)
    {
        if (text is null)
        {
            return fallback;
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan value) && value > TimeSpan.Zero)
        {
            return value;
        }

        errors.Add($"{name} ('{code}'): {property} '{text}' is not a positive hh:mm:ss duration.");
        return fallback;
    }

    private static Type[] HandlerPayloads(Type handlerType, Type openInterface)
        => [.. handlerType.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface).Select(i => i.GetGenericArguments()[0])];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*\.v[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,49}$", RegexOptions.CultureInvariant)]
    private static partial Regex QueuePattern();
}