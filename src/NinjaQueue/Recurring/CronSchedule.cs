using Cronos;

namespace NinjaQueue.Recurring;

/// <summary>
/// A cron expression evaluated in an IANA time zone with Cronos (§5.5, §14.6). DST follows Cronos: in a spring-forward gap
/// a skipped local time fires at the next valid instant (the end of the gap); in a fall-back overlap a fixed local time
/// fires once (the first instance), while interval fields (<c>*/15</c>) keep firing through both instances. Every instant
/// is UTC; no local time is ever persisted.
/// </summary>
public sealed class CronSchedule
{
    /// <summary>Stored when a schedule has no future occurrence; the scheduler never reaches it.</summary>
    public static readonly DateTimeOffset Never = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private readonly CronExpression _expression;

    private CronSchedule(string cron, CronExpression expression, TimeZoneInfo zone)
    {
        Cron = cron;
        _expression = expression;
        Zone = zone;
    }

    /// <summary>The expression as written (5 fields, or 6 with seconds).</summary>
    public string Cron { get; }

    /// <summary>The time zone the expression is evaluated in.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>
    /// Parses <paramref name="cron"/> (5 fields: minute hour day month weekday; or 6 with a leading seconds field) and resolves
    /// <paramref name="timeZone"/> (an IANA id such as <c>Asia/Karachi</c>, or <c>UTC</c>). Throws <see cref="ArgumentException"/>
    /// on an invalid expression or an unknown zone.
    /// </summary>
    public static CronSchedule Parse(string cron, string timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        string trimmed = cron.Trim();
        int fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        CronFormat format = fields switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => throw new ArgumentException($"Cron '{cron}' must have 5 fields (minute hour day month weekday) or 6 with leading seconds.", nameof(cron)),
        };

        CronExpression expression;
        try
        {
            expression = CronExpression.Parse(trimmed, format);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Cron '{cron}' is not valid: {ex.Message}", nameof(cron), ex);
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Time zone '{timeZone}' is not a known IANA time zone id.", nameof(timeZone), ex);
        }

        return new CronSchedule(trimmed, expression, zone);
    }

    /// <summary>The first occurrence strictly after <paramref name="after"/>, or <see cref="Never"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset after)
        => after >= Never ? Never : _expression.GetNextOccurrence(after.ToUniversalTime(), Zone, inclusive: false)?.ToUniversalTime() ?? Never;

    /// <summary>
    /// The latest occurrence in [<paramref name="floor"/>, <paramref name="now"/>], or <paramref name="floor"/> itself when the
    /// expression has none there (the stored <c>next_run_at_utc</c> is always treated as an occurrence). Bounded work: a
    /// doubling search for a window that holds an occurrence, then a binary search to the second, so a long outage of an
    /// every-second schedule costs a few dozen evaluations, not one per missed occurrence.
    /// </summary>
    public DateTimeOffset LatestAtOrBefore(DateTimeOffset now, DateTimeOffset floor)
    {
        if (floor > now)
        {
            throw new ArgumentOutOfRangeException(nameof(floor), "The floor must not be after now.");
        }

        TimeSpan window = Second;
        while (true)
        {
            bool reachedFloor = now - window <= floor;
            DateTimeOffset from = reachedFloor ? floor - TimeSpan.FromTicks(1) : now - window;
            DateTimeOffset first = NextAfter(from);
            if (first <= now)
            {
                // NextAfter(lo) <= now and NextAfter(hi) > now; occurrences are whole seconds, so narrowing to one second
                // leaves exactly the latest occurrence in (lo, hi].
                DateTimeOffset lo = from;
                DateTimeOffset hi = now;
                while (hi - lo > Second)
                {
                    DateTimeOffset mid = lo + ((hi - lo) / 2);
                    if (NextAfter(mid) <= now)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                DateTimeOffset latest = NextAfter(lo);
                return latest < floor ? floor : latest;
            }

            if (reachedFloor)
            {
                return floor;
            }

            window *= 2;
        }
    }

    /// <summary>
    /// The occurrences in [<paramref name="floor"/>, <paramref name="now"/>] (<paramref name="floor"/> counted as one), the latest
    /// <paramref name="max"/> of them in ascending order. Walks at most <paramref name="maxSteps"/> occurrences; beyond that
    /// (an every-second schedule after a day of downtime) it returns only the latest occurrence and sets
    /// <paramref name="truncated"/>.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Between(DateTimeOffset floor, DateTimeOffset now, int max, out bool truncated, int maxSteps = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        truncated = false;
        Queue<DateTimeOffset> window = new(Math.Min(max, 1024));
        int steps = 0;
        int total = 0;
        for (DateTimeOffset at = floor; at <= now; at = NextAfter(at))
        {
            if (++steps > maxSteps)
            {
                truncated = true;
                return [LatestAtOrBefore(now, floor)];
            }

            total++;
            if (window.Count == max)
            {
                window.Dequeue();
            }

            window.Enqueue(at);
        }

        truncated = total > max;
        return [.. window];
    }
}

/// <summary>What one due schedule fires now and when it is next due.</summary>
/// <param name="Fire">The occurrences to enqueue, ascending (possibly none).</param>
/// <param name="NextRunAtUtc">The next occurrence strictly after now.</param>
/// <param name="Truncated">True when <see cref="MisfirePolicy.FireAll"/> dropped occurrences beyond its cap.</param>
public sealed record RecurringPlan(IReadOnlyList<DateTimeOffset> Fire, DateTimeOffset NextRunAtUtc, bool Truncated);

/// <summary>The misfire rules (§14.6), as a pure function of the schedule, its stored due time and the database clock.</summary>
public static class RecurringPlanner
{
    /// <summary>
    /// Plans one due schedule. <paramref name="firstDue"/> is the stored <c>next_run_at_utc</c> (≤ <paramref name="now"/>). An
    /// occurrence older than <paramref name="misfireThreshold"/> is missed:
    /// <list type="bullet">
    /// <item><see cref="MisfirePolicy.FireOnce"/>: the latest due occurrence (on time or missed).</item>
    /// <item><see cref="MisfirePolicy.FireAll"/>: every due occurrence, the latest <paramref name="fireAllCap"/> of them.</item>
    /// <item><see cref="MisfirePolicy.Skip"/>: the latest due occurrence only if it is on time; missed ones never fire.</item>
    /// </list>
    /// In every case the next run is the first occurrence after <c>max(now, occurrence)</c> = after <paramref name="now"/> (§15.8 step 4).
    /// </summary>
    public static RecurringPlan Plan(
        CronSchedule schedule, DateTimeOffset firstDue, DateTimeOffset now, MisfirePolicy policy, TimeSpan misfireThreshold, int fireAllCap)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        DateTimeOffset next = schedule.NextAfter(now);
        if (firstDue > now)
        {
            return new RecurringPlan([], firstDue, false);
        }

        switch (policy)
        {
            case MisfirePolicy.FireAll:
                IReadOnlyList<DateTimeOffset> all = schedule.Between(firstDue, now, Math.Max(1, fireAllCap), out bool truncated);
                return new RecurringPlan(all, next, truncated);
            case MisfirePolicy.Skip:
                DateTimeOffset current = schedule.LatestAtOrBefore(now, firstDue);
                return new RecurringPlan(now - current <= misfireThreshold ? [current] : [], next, false);
            default:
                return new RecurringPlan([schedule.LatestAtOrBefore(now, firstDue)], next, false);
        }
    }
}