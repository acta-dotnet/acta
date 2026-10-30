using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// <see cref="ScheduleEvaluator"/> against each shape of cursor: not due, due on time, due late under each misfire
/// policy, parked on a busy or unavailable slot, every pause shape, an anchored interval, an exhausted or broken
/// source. The decision is the whole of what the admitter writes, so each fact asserts the fields that move.
/// </summary>
public class ScheduleEvaluatorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private static DateTime Utc(int hour, int minute, int second = 0) => new(2024, 1, 2, hour, minute, second, DateTimeKind.Utc);

    private static readonly ScheduleTiming EveryMinute = new("* * * * *", ScheduleExpressionKindCode.Cron, "UTC", null);

    private static readonly ScheduleTiming Daily = new("0 9 * * *", ScheduleExpressionKindCode.Cron, "UTC", null);

    private static ScheduleCursor Active(DateTime? next) => new(next, ScheduleStatusCode.Active, null);

    private static ScheduleEvaluation Evaluate(
        ScheduleTiming timing,
        ScheduleCursor cursor,
        DateTime now,
        MisfireStrategyCode misfire = MisfireStrategyCode.Skip,
        AdmissionCapacity capacity = AdmissionCapacity.Available,
        TimeSpan? expireAfter = null,
        ScheduleExpireActionCode expireAction = ScheduleExpireActionCode.Unspecified,
        IActaCalendar? calendar = null
    ) =>
        ScheduleEvaluator.Evaluate(
            new ScheduleSource(timing, calendar, null),
            new SchedulePolicy(misfire, Grace, expireAfter, expireAction),
            cursor,
            capacity,
            now
        );

    [Fact]
    public void An_occurrence_ahead_is_the_next_evaluation_and_moves_nothing()
    {
        var cursor = Active(Utc(10, 5));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 4, 30));

        Assert.Null(d.Admit);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
        Assert.Equal(Utc(10, 5), d.NextEvaluationAtUtc);
        Assert.False(d.Changes(cursor, EvaluationStatusCode.Scheduled, Utc(10, 5)));
    }

    [Fact]
    public void A_due_occurrence_within_the_grace_is_admitted_and_the_cursor_moves_past_it()
    {
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 5, 20));

        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), d.Admit);
        Assert.Null(d.SkippedFromUtc);
        Assert.Equal(Utc(10, 6), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(10, 6), d.NextEvaluationAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
    }

    [Fact]
    public void Skip_admits_the_latest_due_occurrence_and_audits_the_earlier_ones_as_skipped()
    {
        // 10:05, 10:06, and 10:07 are due; only 10:07 is within the grace of 10:07:30.
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 7, 30));

        Assert.Equal(new AdmittedOccurrence(Utc(10, 7), Utc(10, 7)), d.Admit);
        Assert.Equal(Utc(10, 5), d.SkippedFromUtc);
        Assert.Equal(Utc(10, 6), d.SkippedThroughUtc);
        Assert.Equal(Utc(10, 8), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void Skip_discards_an_occurrence_missed_beyond_the_grace_and_waits_for_the_next()
    {
        var d = Evaluate(Daily, Active(Utc(9, 0)), Utc(11, 0));

        Assert.Null(d.Admit);
        Assert.Equal(Utc(9, 0), d.SkippedFromUtc);
        Assert.Equal(Utc(9, 0), d.SkippedThroughUtc);
        Assert.Equal(Utc(9, 0).AddDays(1), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(9, 0).AddDays(1), d.NextEvaluationAtUtc);
    }

    [Fact]
    public void CatchUpOnce_admits_the_earliest_due_occurrence_covering_through_the_latest()
    {
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 9, 30), MisfireStrategyCode.CatchUpOnce);

        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 9)), d.Admit);
        Assert.Null(d.SkippedFromUtc);
        Assert.Equal(Utc(10, 10), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_busy_slot_parks_the_schedule_until_settlement_wakes_it()
    {
        var cursor = Active(Utc(10, 5));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 7), capacity: AdmissionCapacity.Full);

        Assert.Null(d.Admit);
        Assert.Equal(EvaluationStatusCode.WaitingForCapacity, d.Status);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Equal(cursor.NextOccurrenceAtUtc, d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void An_unavailable_slot_parks_the_schedule_until_the_job_is_resumed()
    {
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 7), capacity: AdmissionCapacity.Unavailable);

        Assert.Null(d.Admit);
        Assert.Equal(EvaluationStatusCode.SlotUnavailable, d.Status);
        Assert.Null(d.NextEvaluationAtUtc);
    }

    [Fact]
    public void An_indefinite_pause_has_no_next_evaluation_and_keeps_its_cursor()
    {
        var cursor = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, null);

        var d = Evaluate(EveryMinute, cursor, Utc(10, 30));

        Assert.Null(d.Admit);
        Assert.False(d.PauseEnded);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Equal(Utc(10, 5), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_timed_pause_ahead_wakes_at_its_end()
    {
        var cursor = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, Utc(10, 20));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 7));

        Assert.False(d.PauseEnded);
        Assert.Equal(Utc(10, 20), d.NextEvaluationAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
    }

    [Fact]
    public void An_elapsed_timed_pause_skips_the_occurrences_inside_its_window_under_Skip()
    {
        // Paused at 10:05 until 10:20:30; now is 10:20:40, so 10:05 through 10:20 fell inside the window.
        var cursor = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, Utc(10, 20, 30));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 20, 40));

        Assert.True(d.PauseEnded);
        Assert.Null(d.Admit);
        Assert.Equal(Utc(10, 5), d.SkippedFromUtc);
        Assert.Equal(Utc(10, 20), d.SkippedThroughUtc);
        Assert.Equal(Utc(10, 21), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(10, 21), d.NextEvaluationAtUtc);
    }

    [Fact]
    public void An_elapsed_timed_pause_catches_up_once_on_the_occurrences_inside_its_window()
    {
        var cursor = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, Utc(10, 20, 30));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 20, 40), MisfireStrategyCode.CatchUpOnce);

        Assert.True(d.PauseEnded);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 20)), d.Admit);
        Assert.Equal(Utc(10, 21), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_pause_ending_on_an_occurrence_admits_it()
    {
        var cursor = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, Utc(10, 5));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 5, 10));

        Assert.True(d.PauseEnded);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), d.Admit);
        Assert.Null(d.SkippedFromUtc);
    }

    [Fact]
    public void An_elapsed_timed_pause_with_nothing_due_resumes_without_admitting()
    {
        var cursor = new ScheduleCursor(Utc(10, 30), ScheduleStatusCode.Paused, Utc(10, 20));

        var d = Evaluate(EveryMinute, cursor, Utc(10, 21));

        Assert.True(d.PauseEnded);
        Assert.Null(d.Admit);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
        Assert.Equal(Utc(10, 30), d.NextEvaluationAtUtc);
        Assert.True(d.Changes(cursor, EvaluationStatusCode.Scheduled, Utc(10, 30)));
    }

    [Fact]
    public void An_anchored_interval_steps_from_its_anchor_however_late_the_evaluation_is()
    {
        var timing = new ScheduleTiming("PT10M", ScheduleExpressionKindCode.Interval, null, Utc(9, 0));

        // Due at 9:10; evaluated at 9:33:30 the latest tick is 9:30 and the next is 9:40, not 9:43:30.
        var d = Evaluate(timing, Active(Utc(9, 10)), Utc(9, 33, 30), MisfireStrategyCode.CatchUpOnce);

        Assert.Equal(new AdmittedOccurrence(Utc(9, 10), Utc(9, 30)), d.Admit);
        Assert.Equal(Utc(9, 40), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void An_exhausted_source_has_no_next_evaluation()
    {
        var never = new ScheduleTiming("0 0 30 2 *", ScheduleExpressionKindCode.Cron, "UTC", null);

        var d = Evaluate(never, Active(null), Utc(10, 0));

        Assert.Equal(EvaluationStatusCode.Exhausted, d.Status);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Null(d.Admit);
    }

    [Fact]
    public void A_source_that_cannot_be_evaluated_keeps_its_cursor_and_retries_after_the_delay()
    {
        var broken = new ScheduleTiming("not a cron", ScheduleExpressionKindCode.Cron, "UTC", null);
        var cursor = Active(Utc(10, 5));

        var d = Evaluate(broken, cursor, Utc(10, 6));

        Assert.Equal(EvaluationStatusCode.EvaluationError, d.Status);
        Assert.Equal(Utc(10, 6) + ScheduleEvaluator.ErrorRetryDelay, d.NextEvaluationAtUtc);
        Assert.Equal(cursor.NextOccurrenceAtUtc, d.NextOccurrenceAtUtc);
        Assert.Null(d.Admit);
    }

    [Fact]
    public void The_fingerprint_follows_the_expression_the_zone_and_the_anchor_and_nothing_else()
    {
        var a = TimingFingerprint.Compute(new ScheduleTiming("0 9 * * *", ScheduleExpressionKindCode.Cron, "Europe/Ljubljana", null));

        Assert.Equal(
            a,
            TimingFingerprint.Compute(new ScheduleTiming("0 9 * * *", ScheduleExpressionKindCode.Cron, "Europe/Ljubljana", null))
        );
        Assert.NotEqual(
            a,
            TimingFingerprint.Compute(new ScheduleTiming("0 8 * * *", ScheduleExpressionKindCode.Cron, "Europe/Ljubljana", null))
        );
        Assert.NotEqual(a, TimingFingerprint.Compute(new ScheduleTiming("0 9 * * *", ScheduleExpressionKindCode.Cron, "UTC", null)));
        var anchored = TimingFingerprint.Compute(new ScheduleTiming("PT5M", ScheduleExpressionKindCode.Interval, null, Utc(9, 0)));
        Assert.NotEqual(
            anchored,
            TimingFingerprint.Compute(new ScheduleTiming("PT5M", ScheduleExpressionKindCode.Interval, null, Utc(9, 5)))
        );
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void CatchUpLatest_admits_the_latest_due_occurrence_however_late_and_coalesces_the_earlier_ones()
    {
        // 10:05 through 10:09 are due at 10:12:30, all past the grace; the latest fires and the rest are a skipped range.
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 12, 30), MisfireStrategyCode.CatchUpLatest);

        Assert.Equal(new AdmittedOccurrence(Utc(10, 12), Utc(10, 12)), d.Admit);
        Assert.Equal(Utc(10, 5), d.SkippedFromUtc);
        Assert.Equal(Utc(10, 11), d.SkippedThroughUtc);
        Assert.Equal(Utc(10, 13), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void CatchUpAll_admits_the_earliest_due_occurrence_alone_and_is_due_again_at_once()
    {
        var d = Evaluate(EveryMinute, Active(Utc(10, 5)), Utc(10, 9, 30), MisfireStrategyCode.CatchUpAll);

        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), d.Admit);
        Assert.Null(d.SkippedFromUtc);
        Assert.Equal(Utc(10, 6), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(10, 6), d.NextEvaluationAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
    }

    [Fact]
    public void Expiration_discards_the_occurrences_older_than_the_age_and_admits_the_rest()
    {
        // At 10:10 with a two-minute expiry, 10:05 through 10:08 are expired and 10:09 and 10:10 are due.
        var d = Evaluate(
            EveryMinute,
            Active(Utc(10, 5)),
            Utc(10, 10),
            MisfireStrategyCode.CatchUpAll,
            expireAfter: TimeSpan.FromMinutes(2),
            expireAction: ScheduleExpireActionCode.Discard
        );

        Assert.Equal(Utc(10, 5), d.ExpiredFromUtc);
        Assert.Equal(Utc(10, 8), d.ExpiredThroughUtc);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 9), Utc(10, 9)), d.Admit);
        Assert.Equal(Utc(10, 10), d.NextOccurrenceAtUtc);
        Assert.False(d.PausedByExpiry);
        Assert.Equal(Utc(10, 10), d.NextEvaluationAtUtc);
    }

    [Fact]
    public void Expiration_under_Pause_decides_the_due_range_and_then_pauses_the_schedule()
    {
        var d = Evaluate(
            EveryMinute,
            Active(Utc(10, 5)),
            Utc(10, 10),
            MisfireStrategyCode.Skip,
            expireAfter: TimeSpan.FromMinutes(2),
            expireAction: ScheduleExpireActionCode.Pause
        );

        Assert.Equal(Utc(10, 5), d.ExpiredFromUtc);
        Assert.Equal(Utc(10, 8), d.ExpiredThroughUtc);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 10), Utc(10, 10)), d.Admit);
        Assert.Equal(Utc(10, 9), d.SkippedFromUtc);
        Assert.True(d.PausedByExpiry);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Equal(Utc(10, 11), d.NextOccurrenceAtUtc);
    }

    [Fact]
    public void Expiration_with_nothing_due_left_moves_the_cursor_without_admitting()
    {
        // The 9:00 occurrence is three hours old at noon under a two-hour age, so it expires and tomorrow's is next.
        var d = Evaluate(
            Daily,
            Active(Utc(9, 0)),
            Utc(12, 0),
            MisfireStrategyCode.CatchUpAll,
            expireAfter: TimeSpan.FromHours(2),
            expireAction: ScheduleExpireActionCode.Discard
        );

        Assert.Null(d.Admit);
        Assert.Equal(Utc(9, 0), d.ExpiredFromUtc);
        Assert.Equal(Utc(9, 0), d.ExpiredThroughUtc);
        Assert.Equal(Utc(9, 0).AddDays(1), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(9, 0).AddDays(1), d.NextEvaluationAtUtc);
        Assert.True(d.Changes(Active(Utc(9, 0)), EvaluationStatusCode.Scheduled, Utc(9, 0)));
    }

    [Fact]
    public void A_resume_and_an_elapsed_timed_pause_expire_the_stale_backlog_without_pausing_again()
    {
        var resumed = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Active, null, EvaluationStatusCode.Resumed);
        var afterResume = Evaluate(
            EveryMinute,
            resumed,
            Utc(10, 10),
            MisfireStrategyCode.CatchUpAll,
            expireAfter: TimeSpan.FromMinutes(2),
            expireAction: ScheduleExpireActionCode.Pause
        );
        Assert.Equal(Utc(10, 8), afterResume.ExpiredThroughUtc);
        Assert.False(afterResume.PausedByExpiry);
        Assert.Equal(new AdmittedOccurrence(Utc(10, 9), Utc(10, 9)), afterResume.Admit);

        var timed = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Paused, Utc(10, 9, 30));
        var afterPause = Evaluate(
            EveryMinute,
            timed,
            Utc(10, 10),
            MisfireStrategyCode.CatchUpAll,
            expireAfter: TimeSpan.FromMinutes(2),
            expireAction: ScheduleExpireActionCode.Pause
        );
        Assert.True(afterPause.PauseEnded);
        Assert.False(afterPause.PausedByExpiry);
        Assert.Equal(Utc(10, 8), afterPause.ExpiredThroughUtc);
    }

    [Fact]
    public void A_parked_schedule_expires_its_stale_backlog_at_once_and_a_resumed_one_is_not_paused_for_it()
    {
        // Resumed at 10:10 with the cursor still on 10:05 under a two-minute expiry, while the slot is busy: 10:05
        // through 10:08 expire now, the schedule parks on 10:09, and the resume keeps the Pause action from firing.
        var resumed = new ScheduleCursor(Utc(10, 5), ScheduleStatusCode.Active, null, EvaluationStatusCode.Resumed);
        var d = Evaluate(
            EveryMinute,
            resumed,
            Utc(10, 10),
            MisfireStrategyCode.CatchUpAll,
            AdmissionCapacity.Full,
            TimeSpan.FromMinutes(2),
            ScheduleExpireActionCode.Pause
        );

        Assert.Null(d.Admit);
        Assert.Equal(Utc(10, 5), d.ExpiredFromUtc);
        Assert.Equal(Utc(10, 8), d.ExpiredThroughUtc);
        Assert.False(d.PausedByExpiry);
        Assert.Equal(EvaluationStatusCode.WaitingForCapacity, d.Status);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Equal(Utc(10, 9), d.NextOccurrenceAtUtc);

        // Woken by a settlement half a minute later, nothing stale is left, so the backlog is admitted, not paused.
        var woken = new ScheduleCursor(Utc(10, 9), ScheduleStatusCode.Active, null, EvaluationStatusCode.Scheduled);
        var after = Evaluate(
            EveryMinute,
            woken,
            Utc(10, 10, 30),
            MisfireStrategyCode.CatchUpAll,
            expireAfter: TimeSpan.FromMinutes(2),
            expireAction: ScheduleExpireActionCode.Pause
        );
        Assert.Equal(new AdmittedOccurrence(Utc(10, 9), Utc(10, 9)), after.Admit);
        Assert.False(after.PausedByExpiry);
    }

    [Fact]
    public void A_parked_schedule_whose_backlog_expired_under_Pause_pauses_without_waiting_for_the_slot()
    {
        var d = Evaluate(
            EveryMinute,
            Active(Utc(10, 5)),
            Utc(10, 10),
            MisfireStrategyCode.Skip,
            AdmissionCapacity.Full,
            TimeSpan.FromMinutes(2),
            ScheduleExpireActionCode.Pause
        );

        Assert.Null(d.Admit);
        Assert.True(d.PausedByExpiry);
        Assert.Equal(Utc(10, 8), d.ExpiredThroughUtc);
        Assert.Equal(Utc(10, 9), d.NextOccurrenceAtUtc);
        Assert.Null(d.NextEvaluationAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
    }

    [Fact]
    public void CatchUpAll_admits_from_a_backlog_too_deep_to_walk()
    {
        // Twenty days of per-second occurrences are due, more than one evaluation may walk; CatchUpAll needs only the
        // cursor and the occurrence after it.
        var everySecond = new ScheduleTiming("* * * * * *", ScheduleExpressionKindCode.Cron, "UTC", null);

        var d = Evaluate(everySecond, Active(Utc(10, 5)), Utc(10, 5).AddDays(20), MisfireStrategyCode.CatchUpAll);

        Assert.Equal(new AdmittedOccurrence(Utc(10, 5), Utc(10, 5)), d.Admit);
        Assert.Equal(Utc(10, 5, 1), d.NextOccurrenceAtUtc);
        Assert.Equal(Utc(10, 5, 1), d.NextEvaluationAtUtc);
        Assert.Equal(EvaluationStatusCode.Scheduled, d.Status);
    }

    [Fact]
    public void A_stored_occurrence_the_calendar_no_longer_produces_is_skipped_as_source_changed()
    {
        // The cursor names 10:05, but the calendar now produces only the tens of minutes, so 10:05 is decided without
        // work and the cursor moves to the first instant the calendar does produce, for the next pass to decide.
        var tenMinutes = new ScheduleTiming("ten-minutes", ScheduleExpressionKindCode.Calendar, "UTC", null);

        var d = Evaluate(tenMinutes, Active(Utc(10, 5)), Utc(10, 6), MisfireStrategyCode.CatchUpAll, calendar: new EveryTenMinutes());

        Assert.Null(d.Admit);
        Assert.True(d.SourceChanged);
        Assert.Equal((Utc(10, 5), Utc(10, 5)), (d.SkippedFromUtc, d.SkippedThroughUtc));
        Assert.Equal(Utc(10, 10), d.NextOccurrenceAtUtc);
    }

    private sealed class EveryTenMinutes : IActaCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context)
        {
            var after = context.AfterUtc;
            var hour = new DateTime(after.Year, after.Month, after.Day, after.Hour, 0, 0, DateTimeKind.Utc);
            return hour.AddMinutes((after.Minute / 10 + 1) * 10);
        }
    }

    [Fact]
    public void An_interval_finer_than_a_millisecond_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => NextOccurrenceCalculator.ParseInterval("PT1.0000001S"));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), NextOccurrenceCalculator.ParseInterval("PT1.5S"));
    }
}
