using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What an evaluation reads from a due backlog: the latest due occurrence, and the one before it that ends a skipped
/// range. Checked against two plain walks, kept here as the reference, over crons, calendars, availability-filtered
/// sources, and grid intervals, across DST transitions and backlogs from none to decades deep.
/// </summary>
public sealed class ScheduleDueWalkTests
{
    private const string Ljubljana = "Europe/Ljubljana";
    private const string NewYork = "America/New_York";

    private const string OfficeHours = """
        {"kind":"weekly-availability","timeZone":"schedule",
         "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday"],"windows":[["09:00","12:00"],["13:00","17:00"]]}],
         "dateOverrides":{"2027-01-08":[["09:00","12:00"]],"2027-01-11":[]}}
        """;

    private const string Nights = """
        {"kind":"weekly-availability","timeZone":"schedule",
         "weekly":[{"days":["Saturday","Sunday"],"windows":[["00:00","04:00"]]}]}
        """;

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // The cursor's seeds: an ordinary Monday, the eve of Europe's and of America's spring-forward and fall-back nights.
    private static readonly DateTime[] Seeds =
    [
        Utc(2027, 1, 4, 5),
        Utc(2027, 3, 13, 22),
        Utc(2027, 3, 27, 22),
        Utc(2027, 10, 30, 22),
        Utc(2027, 11, 6, 22),
    ];

    // How far past the cursor the evaluation runs: on time, a minute late, hours, days, and over a week of backlog.
    private static readonly TimeSpan[] Backlogs =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(59),
        TimeSpan.FromHours(5),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(9),
    ];

    public static TheoryData<string> Sources => [.. SourceByName.Keys];

    private static readonly Dictionary<string, ScheduleSource> SourceByName = new()
    {
        ["cron every minute"] = Cron("* * * * *", "UTC"),
        ["cron with seconds"] = Cron("0,30 */10 * * * *", "UTC"),
        ["cron weekdays at nine"] = Cron("0 9 * * 1-5", Ljubljana),
        ["cron in the gap"] = Cron("30 2 * * *", Ljubljana),
        ["cron in the fold"] = Cron("*/20 1 * * *", NewYork),
        ["cron hourly across both changes"] = Cron("0 * * * *", NewYork),
        // Ireland's zone data has winter as the shifted time, which turns the order of a fold's two passes around.
        ["cron in a fold whose winter is the shifted time"] = Cron("*/20 * * * *", "Europe/Dublin"),
        ["cron weekly"] = Cron("0 6 * * 1", Ljubljana),
        ["cron that ends"] = new(
            new ScheduleTiming("*/5 * * * *", ScheduleExpressionKindCode.Cron, "UTC", null, EndBeforeUtc: Utc(2027, 3, 28, 1)),
            null,
            null
        ),
        ["calendar daily in the gap"] = Calendar("""{"kind":"daily-local-time","times":["02:30","14:15"]}""", Ljubljana),
        ["calendar second business day of the week"] = Calendar(
            """{"kind":"business-day","timeZone":"UTC","select":"first","of":"week","nth":2,"holidays":["2027-01-05"],"time":"09:00"}""",
            "UTC"
        ),
        ["calendar instants"] = Calendar(
            """{"kind":"explicit-instants","instants":["2027-01-04T10:00:00Z","2027-01-05T10:00:00Z","2027-03-28T01:30:00Z","2027-11-07T06:00:00Z"]}""",
            "UTC"
        ),
        ["cron within office hours"] = Available("*/10 * * * *", ScheduleExpressionKindCode.Cron, OfficeHours, Ljubljana),
        ["interval within office hours"] = Available("PT25M", ScheduleExpressionKindCode.Interval, OfficeHours, Ljubljana, Utc(2027, 1, 1)),
        ["calendar within weekend nights"] = new(
            new ScheduleTiming("daily", ScheduleExpressionKindCode.Calendar, NewYork, null, AvailabilityCalendarName: "nights"),
            Occurrence("""{"kind":"daily-local-time","times":["00:30","01:30","03:45","12:00"]}"""),
            Availability(Nights)
        ),
        ["interval anchored"] = new(new ScheduleTiming("PT7M", ScheduleExpressionKindCode.Interval, null, Utc(2027, 1, 1)), null, null),
        ["interval unanchored"] = new(new ScheduleTiming("PT1H", ScheduleExpressionKindCode.Interval, null, null), null, null),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void One_walk_answers_what_the_two_walks_answered(string name)
    {
        var source = SourceByName[name];
        var ranges = 0;
        foreach (var seed in Seeds)
        {
            if (NextOccurrenceCalculator.Next(source, seed) is not { } from)
            {
                continue;
            }

            foreach (var backlog in Backlogs)
            {
                var now = from + backlog;
                var latest = ReferenceLatestDue(source, from, now);
                DateTime? previous = from < latest ? ReferencePreviousDue(source, from, latest) : null;
                ranges += previous is null ? 0 : 1;
                var context = $"{name}, cursor {from:O}, now {now:O}";

                var due = ScheduleEvaluator.LatestDue(source, from, now);
                Assert.True(latest == due.Latest, $"{context}: latest {due.Latest:O}, expected {latest:O}");
                // With no steps the search answers alone, which a backlog short of DueSteps would never ask of it.
                var searched = ScheduleEvaluator.LatestDue(source, from, now, dueSteps: 0);
                Assert.True(due == searched, $"{context}: searched {searched.Latest:O} after {searched.Before:O}, stepped {due.Latest:O}");
                if (source.Timing.ExpressionKind != ScheduleExpressionKindCode.Interval || source.Availability is not null)
                {
                    Assert.True(previous == due.Before, $"{context}: before {due.Before:O}, expected {previous:O}");
                }

                var following = NextOccurrenceCalculator.FirstAfter(source, source.Timing.IntervalAnchorAtUtc ?? latest, now);
                var skipped = previous is null ? ((DateTime?)null, (DateTime?)null) : (from, previous);
                AssertEvaluation(
                    context,
                    Evaluate(source, MisfireStrategyCode.CatchUpOnce, from, now),
                    (from, latest),
                    (null, null),
                    following
                );
                AssertEvaluation(
                    context,
                    Evaluate(source, MisfireStrategyCode.CatchUpLatest, from, now),
                    (latest, latest),
                    skipped,
                    following
                );
                AssertEvaluation(
                    context,
                    Evaluate(source, MisfireStrategyCode.Skip, from, now, TimeSpan.FromDays(365)),
                    (latest, latest),
                    skipped,
                    following
                );
                AssertEvaluation(
                    context,
                    Evaluate(source, MisfireStrategyCode.Skip, from, now, TimeSpan.Zero),
                    latest == now ? (latest, latest) : null,
                    latest == now ? skipped : (from, latest),
                    following
                );
            }
        }

        Assert.True(ranges > 0, $"{name}: no backlog held two occurrences, so the occurrence before the latest was never compared");
    }

    // A cron with seconds a month behind has over two million occurrences due, more than any walk should take.
    [Fact]
    public void A_backlog_past_a_million_occurrences_is_decided_like_any_other()
    {
        var source = Cron("* * * * * *", "UTC");
        var from = Utc(2027, 1, 4, 5);
        var latest = from.AddDays(30);
        var now = latest.AddMilliseconds(250);
        var previous = latest.AddSeconds(-1);
        var following = latest.AddSeconds(1);

        Assert.Equal((latest, (DateTime?)previous), ScheduleEvaluator.LatestDue(source, from, now));
        AssertEvaluation("once", Evaluate(source, MisfireStrategyCode.CatchUpOnce, from, now), (from, latest), (null, null), following);
        AssertEvaluation(
            "latest",
            Evaluate(source, MisfireStrategyCode.CatchUpLatest, from, now),
            (latest, latest),
            (from, previous),
            following
        );
        AssertEvaluation("skip", Evaluate(source, MisfireStrategyCode.Skip, from, now), (latest, latest), (from, previous), following);
    }

    // Registration anchors every interval, so only the search is held to an unanchored one: it must stay on the
    // cursor's grid, where a seek from an arbitrary instant would start a grid of its own.
    [Fact]
    public void An_unanchored_interval_within_availability_is_searched_on_the_cursors_grid()
    {
        var source = Available("PT25M", ScheduleExpressionKindCode.Interval, OfficeHours, Ljubljana);
        var from = Assert.IsType<DateTime>(NextOccurrenceCalculator.Next(source, Utc(2027, 1, 4, 5)));
        var latest = ReferenceLatestDue(source, from, from.AddDays(9));

        var due = ScheduleEvaluator.LatestDue(source, from, from.AddDays(9), dueSteps: 0);

        Assert.Equal(latest, due.Latest);
        Assert.Equal(ReferencePreviousDue(source, from, latest), due.Before);
    }

    // Samoa skipped 30 December 2011, and a daily cron there can answer an instant that does not advance. Stepped or
    // searched, the evaluation ends: decided, or refused as a source that does not advance.
    [Theory]
    [InlineData(0)]
    [InlineData(ScheduleEvaluator.DueSteps)]
    public async Task A_cron_that_does_not_advance_ends_its_evaluation(int dueSteps)
    {
        var source = Cron("0 0 * * *", "Pacific/Apia");
        var from = new DateTime(2011, 12, 1, 10, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2011, 12, 31, 11, 0, 0, DateTimeKind.Utc);

        var ended = Task.Run(() => Record.Exception(() => ScheduleEvaluator.LatestDue(source, from, now, dueSteps)));

        var thrown = await ended.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(thrown is null or InvalidOperationException, $"{thrown}");
    }

    // The steps hand over to the search at any depth: exactly as many due as steps, one more, one fewer.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_search_takes_over_from_the_steps_at_any_depth(int dueSteps)
    {
        var source = Cron("* * * * *", "UTC");
        var from = Utc(2027, 1, 4, 5);
        for (var due = 0; due <= 5; due++)
        {
            var latest = from.AddMinutes(due);
            DateTime? before = due == 0 ? null : latest.AddMinutes(-1);

            Assert.Equal((latest, before), ScheduleEvaluator.LatestDue(source, from, latest.AddSeconds(10), dueSteps));
        }
    }

    [Fact]
    public void A_backlog_of_any_depth_costs_a_bounded_number_of_seeks()
    {
        var everySecond = new EverySecond();
        var source = new ScheduleSource(
            new ScheduleTiming("every-second", ScheduleExpressionKindCode.Calendar, "UTC", null),
            everySecond,
            null
        );
        var from = Utc(2027, 1, 4, 5);
        var latest = from.AddDays(30);

        var due = ScheduleEvaluator.LatestDue(source, from, latest.AddMilliseconds(250));

        Assert.Equal((latest, (DateTime?)latest.AddSeconds(-1)), due);
        // Two searches of a span of ticks, each at most one seek per bit of the span and the step's own seek first.
        Assert.InRange(everySecond.Seeks, 1, ScheduleEvaluator.DueSteps + 130);
    }

    private sealed class EverySecond : IActaCalendar
    {
        public int Seeks { get; private set; }

        public DateTime? GetNextOccurrence(CalendarContext context)
        {
            Seeks++;
            return new DateTime((context.AfterUtc.Ticks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }

    [Fact]
    public void A_backlog_of_decades_matches_the_reference_walks()
    {
        var source = Cron("0 9 * * 1-5", Ljubljana);
        var from = Assert.IsType<DateTime>(NextOccurrenceCalculator.Next(source, Utc(2027, 1, 4, 5)));
        var now = from.AddYears(30).AddHours(3);
        var latest = ReferenceLatestDue(source, from, now);

        var due = ScheduleEvaluator.LatestDue(source, from, now);

        Assert.Equal(latest, due.Latest);
        Assert.Equal(ReferencePreviousDue(source, from, latest), due.Before);
    }

    private static void AssertEvaluation(
        string context,
        ScheduleEvaluation e,
        (DateTime Occurrence, DateTime CoveredThrough)? admit,
        (DateTime? From, DateTime? Through) skipped,
        DateTime? following
    )
    {
        Assert.Null(e.Error);
        Assert.True(
            Equals(admit is { } a ? new AdmittedOccurrence(a.Occurrence, a.CoveredThrough) : null, e.Admit),
            $"{context}: admitted {e.Admit}, expected {admit}"
        );
        Assert.True(
            skipped == (e.SkippedFromUtc, e.SkippedThroughUtc),
            $"{context}: skipped {e.SkippedFromUtc:O}..{e.SkippedThroughUtc:O}"
        );
        Assert.True(following == e.NextOccurrenceAtUtc, $"{context}: next {e.NextOccurrenceAtUtc:O}, expected {following:O}");
    }

    private static ScheduleEvaluation Evaluate(
        ScheduleSource source,
        MisfireStrategyCode misfire,
        DateTime from,
        DateTime now,
        TimeSpan? grace = null
    ) =>
        ScheduleEvaluator.Evaluate(
            source,
            new SchedulePolicy(misfire, grace ?? TimeSpan.FromMinutes(1), null, ScheduleExpireActionCode.Unspecified),
            new ScheduleCursor(from, ScheduleStatusCode.Active, null),
            AdmissionCapacity.Available,
            now
        );

    /// <summary>The latest due occurrence as the evaluator walked it before the walk answered the one before it too.</summary>
    private static DateTime ReferenceLatestDue(ScheduleSource source, DateTime fromUtc, DateTime nowUtc)
    {
        var timing = source.Timing;
        if (timing.ExpressionKind == ScheduleExpressionKindCode.Interval && source.Availability is null)
        {
            var interval = NextOccurrenceCalculator.ParseInterval(timing.Expression);
            var anchor = DateTime.SpecifyKind(timing.IntervalAnchorAtUtc ?? fromUtc, DateTimeKind.Utc);
            var limit = timing.EndBeforeUtc is { } end && end <= nowUtc ? end.AddTicks(-1) : nowUtc;
            var elapsed = (limit - anchor).Ticks;
            var steps = elapsed >= 0 ? elapsed / interval.Ticks : -((-elapsed + interval.Ticks - 1) / interval.Ticks);
            var latest = anchor.AddTicks(interval.Ticks * steps);
            return latest < fromUtc ? fromUtc : latest;
        }

        var current = fromUtc;
        while (NextOccurrenceCalculator.Next(source, current) is { } candidate && candidate <= nowUtc)
        {
            current = candidate;
        }

        return current;
    }

    /// <summary>The occurrence before the latest as the evaluator found it before, by a second walk from the cursor.</summary>
    private static DateTime ReferencePreviousDue(ScheduleSource source, DateTime fromUtc, DateTime latestUtc)
    {
        if (source.Timing.ExpressionKind == ScheduleExpressionKindCode.Interval && source.Availability is null)
        {
            return latestUtc - NextOccurrenceCalculator.ParseInterval(source.Timing.Expression);
        }

        var current = fromUtc;
        while (NextOccurrenceCalculator.Next(source, current) is { } candidate && candidate < latestUtc)
        {
            current = candidate;
        }

        return current;
    }

    private static ScheduleSource Cron(string expression, string zone) =>
        new(new ScheduleTiming(expression, ScheduleExpressionKindCode.Cron, zone, null), null, null);

    private static ScheduleSource Calendar(string json, string zone) =>
        new(new ScheduleTiming("calendar", ScheduleExpressionKindCode.Calendar, zone, null), Occurrence(json), null);

    private static ScheduleSource Available(
        string expression,
        ScheduleExpressionKindCode kind,
        string availability,
        string zone,
        DateTime? anchor = null
    ) => new(new ScheduleTiming(expression, kind, zone, anchor, AvailabilityCalendarName: "office"), null, Availability(availability));

    private static IActaCalendar Occurrence(string json) => CalendarDefinition.Compile(CalendarRoleCode.Occurrence, json).Calendar!;

    private static IActaAvailabilityCalendar Availability(string json) =>
        CalendarDefinition.Compile(CalendarRoleCode.Availability, json).Availability!;
}
