using Cronos;
using Xunit;
using Rule = System.TimeZoneInfo.AdjustmentRule;
using Transition = System.TimeZoneInfo.TransitionTime;

namespace Acta.Tests.Runtime;

/// <summary>
/// A zone stated with negative daylight time is computed in its equal with ordinary daylight time. Europe/London keeps
/// Dublin's clock and is stated the ordinary way round on every host, so it says what each answer should be. The
/// hand-built zone holds the rule Ubuntu 26.04 gives Europe/Dublin, so its facts run where the host's own data is
/// ordinary; the facts on Europe/Dublin itself say something only where the host's data is not.
/// </summary>
public sealed class NegativeDaylightTimeTests
{
    private const string Dublin = "Europe/Dublin";

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static readonly TimeSpan Winter = TimeSpan.FromHours(-1);

    // Winter starts on the last Sunday of October at 02:00 and ends on the last Sunday of March at 01:00.
    private static readonly Transition WinterStarts = Transition.CreateFloatingDateRule(
        new DateTime(1, 1, 1, 2, 0, 0),
        10,
        5,
        DayOfWeek.Sunday
    );
    private static readonly Transition WinterEnds = Transition.CreateFloatingDateRule(
        new DateTime(1, 1, 1, 1, 0, 0),
        3,
        5,
        DayOfWeek.Sunday
    );

    /// <summary>Irish Standard Time as the base, and winter an hour of daylight time taken off it.</summary>
    private static readonly TimeZoneInfo Stated = Zone(
        "stated-negative",
        Rule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, Winter, WinterStarts, WinterEnds)
    );

    /// <summary>The same clock on a rule that moves its base too: a base of +02:00, an hour off for the rule, and winter an hour off that.</summary>
    private static readonly TimeZoneInfo StatedOnAMovedBase = TimeZoneInfo.CreateCustomTimeZone(
        "Acta.Tests/stated-on-a-moved-base",
        TimeSpan.FromHours(2),
        "stated-on-a-moved-base",
        "IST",
        "GMT",
        [
            Rule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                Winter,
                WinterStarts,
                WinterEnds,
                TimeSpan.FromHours(-1)
            ),
        ]
    );

    /// <summary>The evening before each clock change of 2027, in UTC.</summary>
    private static readonly DateTime[] Evenings = [Utc(2027, 3, 27, 22), Utc(2027, 10, 30, 22)];

    public static TheoryData<string> Crons => ["*/20 * * * *", "0 * * * *", "30 1 * * *", "15 0-3 * * *"];

    [Theory]
    [MemberData(nameof(Crons))]
    public void A_cron_in_a_zone_stated_with_negative_daylight_time_fires_when_Londons_does(string expression)
    {
        var cron = CronExpression.Parse(expression);

        foreach (var corrected in new[] { NegativeDaylightTime.Corrected(Stated), NegativeDaylightTime.Corrected(StatedOnAMovedBase) })
        {
            foreach (var evening in Evenings)
            {
                var (here, there) = (evening, evening);
                for (var step = 0; step < 40; step++)
                {
                    here = cron.GetNextOccurrence(here, corrected, inclusive: false)!.Value;
                    there = cron.GetNextOccurrence(there, London, inclusive: false)!.Value;
                    Assert.Equal(there, here);
                }
            }
        }
    }

    [Fact]
    public void The_corrected_zone_keeps_the_zones_name_and_its_clock_at_every_hour()
    {
        var corrected = NegativeDaylightTime.Corrected(Stated);

        Assert.Equal(Stated.Id, corrected.Id);
        Assert.Equal(TimeSpan.Zero, corrected.BaseUtcOffset);
        Assert.Equal(Stated.BaseUtcOffset + Winter, corrected.BaseUtcOffset);
        Assert.Empty(HoursThatDiffer(Stated, corrected));
        Assert.Same(corrected, NegativeDaylightTime.Corrected(Stated));

        // The base is the winter offset whatever the rule adds to the zone's own.
        var moved = NegativeDaylightTime.Corrected(StatedOnAMovedBase);
        Assert.Equal(TimeSpan.Zero, moved.BaseUtcOffset);
        Assert.Empty(HoursThatDiffer(StatedOnAMovedBase, moved));
    }

    /// <summary>
    /// The correction states the zone's present rule for every year, and that rule may not hold yet: a zone on one
    /// offset until a negative rule starts next year would lose an hour of this winter. Such a zone is left as it is.
    /// </summary>
    [Fact]
    public void A_zone_whose_negative_rule_does_not_give_this_years_clock_is_answered_as_it_is()
    {
        var upcoming = Zone(
            "upcoming-negative",
            Rule.CreateAdjustmentRule(
                new DateTime(DateTime.UtcNow.Year + 1, 1, 1),
                DateTime.MaxValue.Date,
                Winter,
                WinterStarts,
                WinterEnds
            )
        );

        Assert.Same(upcoming, NegativeDaylightTime.Corrected(upcoming));
    }

    /// <summary>
    /// A negative rule may have no ordinary form: fifteen hours taken off is fifteen added the other way round, more
    /// than a zone may add. Such a zone is left as it is, not refused.
    /// </summary>
    [Fact]
    public void A_zone_whose_rule_has_no_ordinary_form_is_answered_as_it_is()
    {
        var beyond = TimeZoneInfo.CreateCustomTimeZone(
            "Acta.Tests/beyond-fourteen-hours",
            TimeSpan.FromHours(13),
            "beyond-fourteen-hours",
            "standard",
            "daylight",
            [Rule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(-15), WinterStarts, WinterEnds)]
        );

        Assert.Same(beyond, NegativeDaylightTime.Corrected(beyond));
    }

    /// <summary>Two zones may share an id, and a host may read a zone anew: each object is answered for itself.</summary>
    [Fact]
    public void Two_zones_with_one_id_are_each_answered_for_themselves()
    {
        var one = TimeZoneInfo.CreateCustomTimeZone("Acta.Tests/shared-id", TimeSpan.Zero, "one", "one");
        var other = TimeZoneInfo.CreateCustomTimeZone("Acta.Tests/shared-id", TimeSpan.FromHours(2), "other", "other");

        Assert.Same(one, NegativeDaylightTime.Corrected(one));
        Assert.Same(other, NegativeDaylightTime.Corrected(other));
    }

    /// <summary>
    /// What needs no correction is answered as it is: ordinary daylight time, negative daylight time that has ended,
    /// and a zone that stays on its negative time, whose rule has no clock change to state the other way round.
    /// </summary>
    [Fact]
    public void A_zone_whose_present_rule_is_not_a_yearly_negative_one_is_answered_as_it_is()
    {
        var ended = Zone(
            "ended-negative",
            Rule.CreateAdjustmentRule(new DateTime(1994, 1, 1), new DateTime(2017, 12, 31), Winter, WinterStarts, WinterEnds)
        );
        var permanent = Zone(
            "permanent-negative",
            Rule.CreateAdjustmentRule(
                new DateTime(2020, 1, 1),
                DateTime.MaxValue.Date,
                Winter,
                Transition.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 1, 1),
                Transition.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 59), 12, 31)
            )
        );

        Assert.Same(London, NegativeDaylightTime.Corrected(London));
        Assert.Same(TimeZoneInfo.Utc, NegativeDaylightTime.Corrected(TimeZoneInfo.Utc));
        Assert.Same(ended, NegativeDaylightTime.Corrected(ended));
        Assert.Same(permanent, NegativeDaylightTime.Corrected(permanent));
    }

    /// <summary>
    /// The correction follows the zone's present rule in every year, so it is the zone only while that rule holds. Where
    /// the host states Dublin with negative daylight time, this holds the rule to the years the data lists one by one.
    /// </summary>
    [Fact]
    public void The_zone_a_Dublin_schedule_is_computed_in_keeps_Dublins_clock_at_every_hour()
    {
        var computed = NextOccurrenceCalculator.ResolveTimeZone(Dublin);

        Assert.Equal(Dublin, computed.Id);
        Assert.Empty(HoursThatDiffer(TimeZoneInfo.FindSystemTimeZoneById(Dublin), computed));
    }

    [Theory]
    [MemberData(nameof(Crons))]
    public void A_Dublin_cron_fires_when_a_London_cron_does(string expression)
    {
        foreach (var evening in Evenings)
        {
            var (here, there) = (evening, evening);
            for (var step = 0; step < 40; step++)
            {
                here = NextOccurrenceCalculator.Next(expression, Dublin, ScheduleExpressionKindCode.Cron, here)!.Value;
                there = NextOccurrenceCalculator.Next(expression, London.Id, ScheduleExpressionKindCode.Cron, there)!.Value;
                Assert.Equal(there, here);
            }
        }
    }

    /// <summary>
    /// A calendar reads its local times in the zone it names or in its schedule's: 01:30 on the day the hour from 01:00
    /// is skipped is the instant time resumes, and on the day it repeats, its earlier instant.
    /// </summary>
    [Fact]
    public void A_calendar_in_Dublin_fires_when_one_in_London_does()
    {
        static IActaCalendar Daily(string zone) =>
            CalendarDefinition
                .Compile(
                    CalendarRoleCode.Occurrence,
                    $$"""{"kind":"daily-local-time","timeZone":"{{zone}}","times":["00:30","01:30","02:30"]}"""
                )
                .Calendar!;
        var (named, schedules, london) = (Daily(Dublin), Daily("schedule"), Daily(London.Id));
        var zone = NextOccurrenceCalculator.ResolveTimeZone(Dublin);

        foreach (var evening in Evenings)
        {
            var at = evening;
            for (var step = 0; step < 6; step++)
            {
                var expected = london.GetNextOccurrence(new CalendarContext(at, TimeZoneInfo.Utc));
                Assert.Equal(expected, named.GetNextOccurrence(new CalendarContext(at, TimeZoneInfo.Utc)));
                Assert.Equal(expected, schedules.GetNextOccurrence(new CalendarContext(at, zone)));
                at = expected!.Value;
            }
        }

        Assert.Equal(Utc(2027, 3, 28, 1), named.GetNextOccurrence(new CalendarContext(Utc(2027, 3, 28, 0, 30), TimeZoneInfo.Utc)));
        Assert.Equal(Utc(2027, 10, 31, 0, 30), named.GetNextOccurrence(new CalendarContext(Utc(2027, 10, 30, 23, 30), TimeZoneInfo.Utc)));
    }

    /// <summary>The hours from 2026 to 2040 at which the two zones are not the same offset from UTC.</summary>
    private static List<DateTime> HoursThatDiffer(TimeZoneInfo zone, TimeZoneInfo other)
    {
        var differ = new List<DateTime>();
        for (var at = Utc(2026, 1, 1, 0); at.Year <= 2040; at = at.AddHours(1))
        {
            if (zone.GetUtcOffset(at) != other.GetUtcOffset(at))
            {
                differ.Add(at);
            }
        }

        return differ;
    }

    private static TimeZoneInfo Zone(string name, Rule rule) =>
        TimeZoneInfo.CreateCustomTimeZone($"Acta.Tests/{name}", TimeSpan.FromHours(1), name, "IST", "GMT", [rule]);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
}
