using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace NinjaQueue;

/// <summary>Per-schedule options for <see cref="NinjaQueueRecurringBuilderExtensions.AddRecurring{TPayload}"/>.</summary>
public sealed class RecurringJobOptions
{
    /// <summary>The queue (default: the job type's queue).</summary>
    public string? Queue { get; init; }

    /// <summary>What happens to occurrences missed during downtime (default <see cref="MisfirePolicy.FireOnce"/>).</summary>
    public MisfirePolicy MisfirePolicy { get; init; } = MisfirePolicy.FireOnce;

    /// <summary>
    /// False (default): an occurrence is skipped, with one <c>RecurringSkippedOverlap</c> event on the still-active previous
    /// job, while the previous occurrence's job is not terminal (§15.8). True: occurrences may run side by side.
    /// </summary>
    public bool AllowOverlap { get; init; }

    /// <summary>
    /// The definition version (≥ 1). Raise it whenever the cron, time zone, payload or policies change: the startup sync
    /// writes a definition only when its version is at least the stored one, so old and new instances of a rolling deploy
    /// do not flip-flop the schedule (§14.6).
    /// </summary>
    public int DefinitionVersion { get; init; } = 1;
}

/// <summary>A schedule registered in code; validated against the registry when the host starts.</summary>
internal sealed record RecurringScheduleDefinition(
    string Code, Type PayloadType, object Payload, string Cron, string TimeZone, RecurringJobOptions Options);

/// <summary>Registers recurring schedules (§14.6).</summary>
public static partial class NinjaQueueRecurringBuilderExtensions
{
    /// <summary>The longest schedule code: <c>recurring:{code}:{occurrenceUtc:O}</c> must fit an idempotency key.</summary>
    public const int MaxCodeLength = 150;

    /// <summary>
    /// Adds a recurring schedule, defined in code and synced to <c>job_recurring</c> at startup:
    /// <code>jobs.AddRecurring("hcm.candidates.inactivity", "0 2 * * *", "Asia/Karachi", new CandidateInactivityScan());</code>
    /// <paramref name="code"/> is the stable schedule id (letters, digits, <c>. _ - :</c>, at most 150 characters).
    /// <paramref name="cron"/> has 5 fields (or 6 with leading seconds) and is evaluated in <paramref name="timeZone"/> (IANA).
    /// The payload's job type must be registered and be a <see cref="JobTenancy.System"/> job type: one global schedule fans out
    /// per tenant with <c>EnqueueManyAsync</c> instead of a schedule per tenant. Invalid input fails here; an unregistered or
    /// tenant-owned job type refuses the host start.
    /// </summary>
    public static NinjaQueueBuilder AddRecurring<TPayload>(
        this NinjaQueueBuilder builder, string code, string cron, string timeZone, TPayload payload, RecurringJobOptions? options = null)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(payload);
        options ??= new RecurringJobOptions();
        if (string.IsNullOrEmpty(code) || code.Length > MaxCodeLength || !CodePattern().IsMatch(code))
        {
            throw new ArgumentException($"Recurring code '{code}' must be 1-{MaxCodeLength} characters of letters, digits, '.', '_', '-' or ':'.", nameof(code));
        }

        if (options.DefinitionVersion < 1)
        {
            throw new ArgumentException("DefinitionVersion must be at least 1.", nameof(options));
        }

        if (!Enum.IsDefined(options.MisfirePolicy))
        {
            throw new ArgumentException($"MisfirePolicy {options.MisfirePolicy} is not valid.", nameof(options));
        }

        // Fails fast on a malformed expression or an unknown zone.
        Recurring.CronSchedule.Parse(cron, timeZone);

        if (builder.Services.Any(d => d.ServiceType == typeof(RecurringScheduleDefinition) && d.ImplementationInstance is RecurringScheduleDefinition existing && existing.Code == code))
        {
            throw new ArgumentException($"Recurring code '{code}' is registered twice.", nameof(code));
        }

        builder.Services.AddSingleton(new RecurringScheduleDefinition(code, typeof(TPayload), payload, cron.Trim(), timeZone, options));
        builder.Services.TryAddSingleton<Recurring.RecurringScheduler>();
        builder.Services.TryAddSingleton<Recurring.IRecurringScheduler>(sp => sp.GetRequiredService<Recurring.RecurringScheduler>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Recurring.RecurringSchedulerService>());
        return builder;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}