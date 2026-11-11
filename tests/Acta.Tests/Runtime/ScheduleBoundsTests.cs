using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A schedule's bounds through <see cref="NextOccurrenceCalculator"/> and <see cref="ScheduleEvaluator"/>: the start is
/// the first instant an occurrence may fall on, the end the first it may not, both on the nominal instant; an interval
/// with a start steps on the start's grid; an availability seek stops at the end instead of hopping windows; and the
/// coalescing policies cover nothing past the end.
/// </summary>
public sealed class ScheduleBoundsTests
{
    private static DateTime Utc(int h, int m, int s = 0) => new(2024, 1, 1, h, m, s, DateTimeKind.Utc);

    private static ScheduleTiming Cron(string expression, DateTime? start = null, DateTime? end = null) =>
        new(expression, ScheduleExpressionKindCode.Cron, "UTC", null, null, start, end);

    private static ScheduleTiming Interval(string expression, DateTime anchor, DateTime? start = null, DateTime? end = null) =>
        new(expression, ScheduleExpressionKindCode.Interval, null, anchor, null, start, end);

    private static ScheduleSource Bare(ScheduleTiming timing) => new(timing, null, null);

    private static ScheduleEvaluation Evaluate(
        ScheduleTiming timing,
        DateTime cursor,
        DateTime now,
        MisfireStrategyCode misfire,
        TimeSpan? expireAfter = null,
        ScheduleExpireActionCode expireAction = ScheduleExpireActionCode.Unspecified
    ) =>
        ScheduleEvaluator.Evaluate(
            Bare(timing),
            new SchedulePolicy(misfire, expireAfter, expireAction),
            new ScheduleCursor(cursor, ScheduleStatusCode.Active, null),
            AdmissionCapacity.Available,
            now
        );

    [Fact]
    public void The_first_occurrence_is_the_first_at_or_after_the_start()
    {
        // Every 5 minutes with a start of 12:05: a seek from 11:00 lands on the start itself, since the cron produces it.
        var onGrid = Bare(Cron("*/5 * * * *", start: Utc(12, 5)));
        Assert.Equal(Utc(12, 5), NextOccurrenceCalculator.Next(onGrid, Utc(11, 0)));

        // A start the cron does not produce is followed by the first instant it does.
        var offGrid = Bare(Cron("*/5 * * * *", start: Utc(12, 7)));
        Assert.Equal(Utc(12, 10), NextOccurrenceCalculator.Next(offGrid, Utc(11, 0)));

        // Inside the bounds the seek is the plain one.
        Assert.Equal(Utc(12, 15), NextOccurrenceCalculator.Next(offGrid, Utc(12, 10)));
    }

    [Fact]
    public void An_instant_at_or_after_the_end_is_null_and_the_one_before_it_is_answered()
    {
        var source = Bare(Cron("*/5 * * * *", end: Utc(12, 20)));

        Assert.Equal(Utc(12, 15), NextOccurrenceCalculator.Next(source, Utc(12, 10)));
        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(12, 15)));
        Assert.Equal([Utc(12, 5), Utc(12, 10), Utc(12, 15)], NextOccurrenceCalculator.Walk(source, Utc(12, 0), 10));
    }

    [Fact]
    public void An_interval_with_a_start_steps_on_the_starts_grid()
    {
        // The registration plan anchors a started interval at its start, so the grid is start + k * interval.
        var declared = new ScheduleDescriptor(
            "push",
            "tick",
            "PT10M",
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Interval,
            null,
            [],
            StartAtUtc: Utc(12, 7)
        );
        var planned = Assert.Single(
            ScheduleRegistrationPlan.Plan([declared], new Dictionary<string, StoredScheduleState>(), Utc(11, 0), Bare)
        );
        Assert.Equal(Utc(12, 7), planned.IntervalAnchorAtUtc);
        Assert.Equal(Utc(12, 7), planned.NextOccurrenceAtUtc);

        // Registered after the start, the first occurrence is the first grid instant after now, on the same grid.
        var late = Assert.Single(
            ScheduleRegistrationPlan.Plan([declared], new Dictionary<string, StoredScheduleState>(), Utc(12, 30), Bare)
        );
        Assert.Equal(Utc(12, 7), late.IntervalAnchorAtUtc);
        Assert.Equal(Utc(12, 37), late.NextOccurrenceAtUtc);

        // A stored anchor from before the start was declared gives way to the start.
        var stored = new Dictionary<string, StoredScheduleState>
        {
            ["tick"] = new(1, "tick", Utc(12, 0), ExpressionKind: ScheduleExpressionKindCode.Interval, IntervalAnchorAtUtc: Utc(9, 0)),
        };
        Assert.Equal(Utc(12, 7), Assert.Single(ScheduleRegistrationPlan.Plan([declared], stored, Utc(11, 0), Bare)).IntervalAnchorAtUtc);
    }

    [Fact]
    public void A_first_interval_anchor_is_a_whole_millisecond_so_the_stored_anchor_keeps_its_fingerprint()
    {
        // SQL Server stores the anchor as datetime2(3). Hashed at the clock's full precision, the anchor read back at the
        // next deploy would change the fingerprint and retime the schedule, dropping its pending occurrences.
        var declared = new ScheduleDescriptor(
            "push",
            "tick",
            "PT10M",
            null,
            MisfireStrategyCode.CatchUpAll,
            ScheduleExpressionKindCode.Interval,
            null,
            []
        );
        var now = Utc(11, 0).AddTicks(1_234_567);
        var first = Assert.Single(ScheduleRegistrationPlan.Plan([declared], new Dictionary<string, StoredScheduleState>(), now, Bare));
        Assert.Equal(Utc(11, 0).AddMilliseconds(123), first.IntervalAnchorAtUtc);

        // Stored as the provider keeps it, and read back at a later deploy with an older cursor still pending.
        var stored = new Dictionary<string, StoredScheduleState>
        {
            ["tick"] = new(
                1,
                "tick",
                Utc(11, 10),
                ExpressionKind: ScheduleExpressionKindCode.Interval,
                IntervalAnchorAtUtc: first.IntervalAnchorAtUtc,
                TimingFingerprint: first.TimingFingerprint
            ),
        };
        var again = Assert.Single(ScheduleRegistrationPlan.Plan([declared], stored, Utc(13, 0), Bare));
        Assert.Equal(first.TimingFingerprint, again.TimingFingerprint);
        Assert.Equal(Utc(11, 10), again.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_schedule_past_its_end_registers_exhausted()
    {
        var declared = new ScheduleDescriptor(
            "push",
            "tick",
            "*/5 * * * *",
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Cron,
            null,
            [],
            EndBeforeUtc: Utc(12, 0)
        );

        var planned = Assert.Single(
            ScheduleRegistrationPlan.Plan([declared], new Dictionary<string, StoredScheduleState>(), Utc(12, 0), Bare)
        );

        Assert.Null(planned.NextOccurrenceAtUtc);
    }

    [Fact]
    public void An_availability_seek_stops_at_the_end_instead_of_hopping_windows_past_it()
    {
        // Hourly ticks, open 13:00 to 14:00 only, and the end at 12:30: the next window starts after the end, so the seek
        // from it answers null rather than crossing closed windows up to its limit and failing the evaluation.
        var timing = Cron("0 * * * *", end: Utc(12, 30)) with
        {
            AvailabilityCalendarName = "office",
        };
        var source = new ScheduleSource(timing, null, new Windows((Utc(13, 0), Utc(14, 0))));

        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(11, 30)));

        // With the end past the window, the tick inside it is the answer as before.
        var open = new ScheduleSource(timing with { EndBeforeUtc = Utc(15, 0) }, null, new Windows((Utc(13, 0), Utc(14, 0))));
        Assert.Equal(Utc(13, 0), NextOccurrenceCalculator.Next(open, Utc(11, 30)));
    }

    [Fact]
    public void A_coalescing_policy_covers_nothing_past_the_end_and_leaves_the_schedule_exhausted()
    {
        // Every minute from 10:05, the end at 10:08, evaluated at 10:20: 10:05 to 10:07 are due, nothing follows.
        var cron = Cron("* * * * *", end: Utc(10, 8));
        var once = Evaluate(cron, Utc(10, 5), Utc(10, 20), MisfireStrategyCode.CatchUpOnce);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 7)), once.Admit);
        Assert.Null(once.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, once.Status);

        var latest = Evaluate(cron, Utc(10, 5), Utc(10, 20), MisfireStrategyCode.CatchUpLatest);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 7), Utc(10, 7)), latest.Admit);
        Assert.Equal(Utc(10, 6), latest.SkippedThroughUtc);

        // The interval shortcut is cut at the end too: 10:05, 10:06, 10:07 on the grid of 10:05.
        var interval = Interval("PT1M", Utc(10, 5), end: Utc(10, 8));
        var covered = Evaluate(interval, Utc(10, 5), Utc(10, 20), MisfireStrategyCode.CatchUpOnce);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 7)), covered.Admit);
        Assert.Null(covered.NextOccurrenceAtUtc);

        // Skip beyond the grace skips the backlog and is exhausted; within the grace the last one before the end fires.
        var skipped = Evaluate(interval, Utc(10, 5), Utc(10, 20), MisfireStrategyCode.Skip);
        Assert.Null(skipped.Admit);
        Assert.Equal(Utc(10, 7), skipped.SkippedThroughUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, skipped.Status);
        var onTime = Evaluate(interval, Utc(10, 5), Utc(10, 7, 30), MisfireStrategyCode.Skip);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 7), Utc(10, 7)), onTime.Admit);
        Assert.Null(onTime.NextOccurrenceAtUtc);
    }

    [Fact]
    public void An_occurrence_before_the_end_is_admitted_after_the_end_under_CatchUpAll()
    {
        // The bound is on the occurrence's instant, not on when it is evaluated: the backlog runs out in order.
        var cron = Cron("* * * * *", end: Utc(10, 7));

        var first = Evaluate(cron, Utc(10, 5), Utc(10, 30), MisfireStrategyCode.CatchUpAll);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), first.Admit);
        Assert.Equal(Utc(10, 6), first.NextOccurrenceAtUtc);

        var last = Evaluate(cron, Utc(10, 6), Utc(10, 30), MisfireStrategyCode.CatchUpAll);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 6), Utc(10, 6)), last.Admit);
        Assert.Null(last.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, last.Status);
    }

    [Fact]
    public void An_expiry_with_the_end_inside_the_backlog_expires_through_the_last_occurrence_and_exhausts()
    {
        // Every minute from 10:05, the end at 10:08, evaluated at 10:30 with a ten-minute expiry: 10:05 to 10:07 are
        // expired, nothing is admitted, and nothing follows.
        var cron = Cron("* * * * *", end: Utc(10, 8));

        var discarded = Evaluate(
            cron,
            Utc(10, 5),
            Utc(10, 30),
            MisfireStrategyCode.CatchUpAll,
            TimeSpan.FromMinutes(10),
            ScheduleExpireActionCode.Discard
        );
        Assert.Null(discarded.Admit);
        Assert.Equal(Utc(10, 5), discarded.ExpiredFromUtc);
        Assert.Equal(Utc(10, 7), discarded.ExpiredThroughUtc);
        Assert.Null(discarded.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, discarded.Status);

        var paused = Evaluate(
            cron,
            Utc(10, 5),
            Utc(10, 30),
            MisfireStrategyCode.CatchUpAll,
            TimeSpan.FromMinutes(10),
            ScheduleExpireActionCode.Pause
        );
        Assert.True(paused.PausedByExpiry);
        Assert.Equal(Utc(10, 7), paused.ExpiredThroughUtc);
        Assert.Null(paused.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_cursor_at_the_start_is_admitted_as_the_sources_own_occurrence()
    {
        // The start itself is an occurrence when the source produces it, so the cursor is not healed as withdrawn.
        var cron = Evaluate(Cron("* * * * *", start: Utc(10, 5)), Utc(10, 5), Utc(10, 5, 20), MisfireStrategyCode.Skip);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), cron.Admit);
        Assert.False(cron.SourceChanged);
        Assert.Equal(Utc(10, 6), cron.NextOccurrenceAtUtc);

        var interval = Evaluate(Interval("PT1M", Utc(10, 5), start: Utc(10, 5)), Utc(10, 5), Utc(10, 5, 20), MisfireStrategyCode.Skip);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), interval.Admit);
        Assert.False(interval.SourceChanged);
        Assert.Equal(Utc(10, 6), interval.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_cursor_outside_the_bounds_is_skipped_and_the_schedule_moves_inside_them()
    {
        // A planted cursor at the end: nothing follows, and the occurrence is audited as one the source does not produce.
        var ended = Evaluate(Cron("* * * * *", end: Utc(10, 8)), Utc(10, 8), Utc(10, 20), MisfireStrategyCode.CatchUpAll);
        Assert.Null(ended.Admit);
        Assert.Equal(Utc(10, 8), ended.SkippedFromUtc);
        Assert.True(ended.SourceChanged);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, ended.Status);

        // A planted cursor before the start moves to the first occurrence at or after it.
        var early = Evaluate(Cron("* * * * *", start: Utc(10, 30)), Utc(10, 5), Utc(10, 20), MisfireStrategyCode.CatchUpAll);
        Assert.Null(early.Admit);
        Assert.Equal(Utc(10, 30), early.NextOccurrenceAtUtc);
    }

    [Fact]
    public void An_interval_anchored_decades_before_its_end_never_covers_the_end()
    {
        // A double division of the span by the interval rounds onto the end's own grid step; whole ticks do not.
        var anchor = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 11, 6, 10, 8, 0, DateTimeKind.Utc);
        var timing = Interval("PT1M", anchor, end: end);

        var d = Evaluate(timing, end.AddMinutes(-3), end.AddMinutes(12), MisfireStrategyCode.CatchUpLatest);

        Assert.Equal(new AdmittedOccurrence(end.AddMinutes(-1), end.AddMinutes(-1)), d.Admit);
        Assert.Null(d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_seek_from_or_past_the_end_answers_null_without_asking_the_source()
    {
        var timing = new ScheduleTiming("fixed", ScheduleExpressionKindCode.Calendar, "UTC", null, null, null, Utc(12, 0));
        var source = new ScheduleSource(timing, new Throwing(), null);

        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(12, 0)));
        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(15, 0)));
        Assert.Empty(NextOccurrenceCalculator.Walk(source, Utc(12, 0), 5));
    }

    [Fact]
    public void The_earliest_representable_start_is_a_valid_bound()
    {
        var source = Bare(Cron("*/5 * * * *", start: DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)));

        Assert.Equal(Utc(12, 5), NextOccurrenceCalculator.Next(source, Utc(12, 0)));
    }

    private sealed class Throwing : IActaCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context) => throw new InvalidOperationException("asked past the end");
    }

    /// <summary>Ordered disjoint windows; the containing one or the earliest later one, null past the last.</summary>
    private sealed class Windows(params (DateTime Start, DateTime? End)[] windows) : IActaAvailabilityCalendar
    {
        public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
        {
            foreach (var (start, end) in windows)
            {
                if (end is null || end > context.AtOrAfterUtc)
                {
                    return new CalendarWindow(start, end);
                }
            }

            return null;
        }
    }
}
