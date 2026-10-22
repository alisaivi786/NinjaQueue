using NinjaQueue.Recurring;

namespace NinjaQueue.Tests.Recurring;

/// <summary>Cron, time-zone, DST and misfire math for recurring schedules (§14.6, §15.8). Pure: no database.</summary>
public sealed class CronScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    [Fact]
    public void Daily_schedule_in_a_zone_without_dst_fires_at_local_time()
    {
        CronSchedule karachi = CronSchedule.Parse("0 2 * * *", "Asia/Karachi");
        karachi.NextAfter(Utc(2026, 10, 11, 12, 0)).Should().Be(Utc(2026, 10, 11, 21, 0), "02:00 PKT is 21:00 UTC the day before");
    }

    [Fact]
    public void Spring_forward_skipped_local_time_fires_at_the_next_valid_instant()
    {
        // America/New_York 2026-03-08: 02:00 EST jumps to 03:00 EDT, so 02:30 does not exist that day.
        CronSchedule s = CronSchedule.Parse("30 2 * * *", "America/New_York");
        s.NextAfter(Utc(2026, 3, 7, 0, 0)).Should().Be(Utc(2026, 3, 7, 7, 30), "02:30 EST");
        s.NextAfter(Utc(2026, 3, 7, 7, 30)).Should().Be(Utc(2026, 3, 8, 7, 0), "the gap fires at its end: 03:00 EDT");
        s.NextAfter(Utc(2026, 3, 8, 7, 0)).Should().Be(Utc(2026, 3, 9, 6, 30), "02:30 EDT the next day");
    }

    [Fact]
    public void Fall_back_repeated_local_time_fires_once()
    {
        // America/New_York 2026-11-01: 02:00 EDT goes back to 01:00 EST, so 01:30 happens twice.
        CronSchedule s = CronSchedule.Parse("30 1 * * *", "America/New_York");
        DateTimeOffset first = s.NextAfter(Utc(2026, 11, 1, 0, 0));
        first.Should().Be(Utc(2026, 11, 1, 5, 30), "01:30 EDT, the first instance");
        s.NextAfter(first).Should().Be(Utc(2026, 11, 2, 6, 30), "the repeated 01:30 EST is not fired; next is tomorrow");
    }

    [Fact]
    public void Interval_schedule_keeps_firing_through_the_fall_back_hour_without_duplicate_instants()
    {
        CronSchedule s = CronSchedule.Parse("*/30 * * * *", "America/New_York");
        List<DateTimeOffset> fired = [];
        DateTimeOffset at = Utc(2026, 11, 1, 4, 59);
        for (int i = 0; i < 6; i++)
        {
            at = s.NextAfter(at);
            fired.Add(at);
        }

        fired.Should().Equal(Utc(2026, 11, 1, 5, 0), Utc(2026, 11, 1, 5, 30), Utc(2026, 11, 1, 6, 0), Utc(2026, 11, 1, 6, 30), Utc(2026, 11, 1, 7, 0), Utc(2026, 11, 1, 7, 30));
        fired.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Europe_spring_forward_and_fall_back()
    {
        CronSchedule s = CronSchedule.Parse("30 1 * * *", "Europe/London");

        // 2026-03-29: 01:00 GMT jumps to 02:00 BST, so 01:30 does not exist.
        s.NextAfter(Utc(2026, 3, 29, 0, 0)).Should().Be(Utc(2026, 3, 29, 1, 0), "the gap fires at its end: 02:00 BST");

        // 2026-10-25: 02:00 BST goes back to 01:00 GMT, so 01:30 happens twice; fired once (01:30 BST = 00:30 UTC).
        DateTimeOffset first = s.NextAfter(Utc(2026, 10, 24, 23, 0));
        first.Should().Be(Utc(2026, 10, 25, 0, 30));
        s.NextAfter(first).Should().Be(Utc(2026, 10, 26, 1, 30));
    }

    [Fact]
    public void Six_field_expressions_include_seconds()
        => CronSchedule.Parse("*/10 * * * * *", "UTC").NextAfter(Utc(2026, 1, 1, 0, 0, 1)).Should().Be(Utc(2026, 1, 1, 0, 0, 10));

    [Theory]
    [InlineData("not a cron", "UTC")]
    [InlineData("61 * * * *", "UTC")]
    [InlineData("* * * *", "UTC")]
    [InlineData("0 2 * * *", "Mars/Olympus_Mons")]
    public void Invalid_expression_or_zone_is_rejected(string cron, string zone)
        => FluentActions.Invoking(() => CronSchedule.Parse(cron, zone)).Should().Throw<ArgumentException>();

    [Fact]
    public void Schedule_without_future_occurrence_returns_never()
        => CronSchedule.Parse("0 0 30 2 *", "UTC").NextAfter(Utc(2026, 1, 1, 0, 0)).Should().Be(CronSchedule.Never);

    [Fact]
    public void Latest_at_or_before_finds_the_latest_occurrence()
    {
        CronSchedule hourly = CronSchedule.Parse("0 * * * *", "UTC");
        hourly.LatestAtOrBefore(Utc(2026, 5, 1, 10, 59, 59), Utc(2026, 5, 1, 0, 0)).Should().Be(Utc(2026, 5, 1, 10, 0));
        hourly.LatestAtOrBefore(Utc(2026, 5, 1, 10, 0), Utc(2026, 5, 1, 0, 0)).Should().Be(Utc(2026, 5, 1, 10, 0), "inclusive of now");
    }

    [Fact]
    public void Latest_at_or_before_returns_the_floor_when_no_occurrence_follows_it()
    {
        CronSchedule daily = CronSchedule.Parse("0 2 * * *", "UTC");
        DateTimeOffset floor = Utc(2026, 5, 1, 3, 17);
        daily.LatestAtOrBefore(Utc(2026, 5, 1, 9, 0), floor).Should().Be(floor);
    }

    [Fact]
    public void Latest_at_or_before_is_bounded_for_an_every_second_schedule_after_a_long_outage()
    {
        CronSchedule everySecond = CronSchedule.Parse("* * * * * *", "UTC");
        DateTimeOffset now = Utc(2026, 5, 31, 12, 0, 0).AddMilliseconds(400);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        everySecond.LatestAtOrBefore(now, Utc(2026, 5, 1, 0, 0)).Should().Be(Utc(2026, 5, 31, 12, 0, 0));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a doubling and binary search, not 2.6 million steps");
    }

    [Fact]
    public void Latest_at_or_before_with_an_irregular_schedule()
    {
        // Every second during 09:xx only; now is 12:00, so the latest is 09:59:59.
        CronSchedule s = CronSchedule.Parse("* * 9 * * *", "UTC");
        s.LatestAtOrBefore(Utc(2026, 5, 1, 12, 0), Utc(2026, 5, 1, 0, 0)).Should().Be(Utc(2026, 5, 1, 9, 59, 59));
    }

    [Fact]
    public void Between_returns_the_latest_max_occurrences_ascending()
    {
        CronSchedule hourly = CronSchedule.Parse("0 * * * *", "UTC");
        IReadOnlyList<DateTimeOffset> all = hourly.Between(Utc(2026, 5, 1, 0, 0), Utc(2026, 5, 1, 5, 30), 100, out bool truncated);
        all.Should().HaveCount(6);
        truncated.Should().BeFalse();

        IReadOnlyList<DateTimeOffset> capped = hourly.Between(Utc(2026, 5, 1, 0, 0), Utc(2026, 5, 1, 5, 30), 3, out truncated);
        capped.Should().Equal(Utc(2026, 5, 1, 3, 0), Utc(2026, 5, 1, 4, 0), Utc(2026, 5, 1, 5, 0));
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Between_falls_back_to_the_latest_occurrence_beyond_the_step_bound()
    {
        CronSchedule everySecond = CronSchedule.Parse("* * * * * *", "UTC");
        IReadOnlyList<DateTimeOffset> result = everySecond.Between(Utc(2026, 5, 1, 0, 0), Utc(2026, 5, 1, 1, 0), 100, out bool truncated, maxSteps: 1000);
        result.Should().Equal(Utc(2026, 5, 1, 1, 0));
        truncated.Should().BeTrue();
    }
}

/// <summary>The misfire policies (§14.6) and the next-run rule (§15.8 step 4).</summary>
public sealed class RecurringPlannerTests
{
    private static readonly CronSchedule Hourly = CronSchedule.Parse("0 * * * *", "UTC");
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(1);

    private static DateTimeOffset Utc(int h, int mi, int s = 0) => new(2026, 5, 1, h, mi, s, TimeSpan.Zero);

    [Theory]
    [InlineData(MisfirePolicy.FireOnce)]
    [InlineData(MisfirePolicy.FireAll)]
    [InlineData(MisfirePolicy.Skip)]
    public void On_time_occurrence_fires_once_under_every_policy(MisfirePolicy policy)
    {
        RecurringPlan plan = RecurringPlanner.Plan(Hourly, Utc(10, 0), Utc(10, 0, 12), policy, Threshold, 100);
        plan.Fire.Should().Equal(Utc(10, 0));
        plan.NextRunAtUtc.Should().Be(Utc(11, 0));
    }

    [Fact]
    public void Fire_once_enqueues_only_the_latest_missed_occurrence()
    {
        RecurringPlan plan = RecurringPlanner.Plan(Hourly, Utc(2, 0), Utc(7, 20), MisfirePolicy.FireOnce, Threshold, 100);
        plan.Fire.Should().Equal(Utc(7, 0));
        plan.NextRunAtUtc.Should().Be(Utc(8, 0));
    }

    [Fact]
    public void Fire_all_enqueues_every_missed_occurrence_up_to_the_cap()
    {
        RecurringPlanner.Plan(Hourly, Utc(2, 0), Utc(7, 20), MisfirePolicy.FireAll, Threshold, 100).Fire
            .Should().Equal(Utc(2, 0), Utc(3, 0), Utc(4, 0), Utc(5, 0), Utc(6, 0), Utc(7, 0));

        RecurringPlan capped = RecurringPlanner.Plan(Hourly, Utc(2, 0), Utc(7, 20), MisfirePolicy.FireAll, Threshold, 2);
        capped.Fire.Should().Equal(Utc(6, 0), Utc(7, 0));
        capped.Truncated.Should().BeTrue();
        capped.NextRunAtUtc.Should().Be(Utc(8, 0));
    }

    [Fact]
    public void Skip_enqueues_nothing_when_every_due_occurrence_was_missed()
    {
        RecurringPlan plan = RecurringPlanner.Plan(Hourly, Utc(2, 0), Utc(7, 20), MisfirePolicy.Skip, Threshold, 100);
        plan.Fire.Should().BeEmpty();
        plan.NextRunAtUtc.Should().Be(Utc(8, 0), "the schedule still advances");
    }

    [Fact]
    public void Skip_still_fires_the_current_occurrence_after_an_outage()
    {
        RecurringPlan plan = RecurringPlanner.Plan(Hourly, Utc(2, 0), Utc(7, 0, 30), MisfirePolicy.Skip, Threshold, 100);
        plan.Fire.Should().Equal(new[] { Utc(7, 0) }, "missed 02:00-06:00 are skipped; 07:00 is on time");
    }

    [Fact]
    public void Not_yet_due_plans_nothing()
        => RecurringPlanner.Plan(Hourly, Utc(11, 0), Utc(10, 30), MisfirePolicy.FireOnce, Threshold, 100).Fire.Should().BeEmpty();

    [Fact]
    public void Occurrence_keys_fit_the_key_limit_for_the_longest_code()
    {
        string code = new('c', NinjaQueueRecurringBuilderExtensions.MaxCodeLength);
        string key = RecurringScheduler.IdempotencyKeyFor(code, Utc(10, 0));
        key.Length.Should().BeLessThanOrEqualTo(JobLimits.MaxKeyLength);
        key.Should().Be($"recurring:{code}:2026-05-01T10:00:00.0000000+00:00");
        RecurringScheduler.UniqueKeyFor("x").Should().Be("recurring:x");
    }
}