using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The catalog's declarative kinds through <see cref="CalendarDefinition.Compile"/>: weekly availability with
/// overnight windows, overrides, and exclusions in a zone across DST; the window algebra; each occurrence kind; the
/// DST gap and fold rules; and the validation that names every problem by its path.
/// </summary>
public sealed class CalendarDefinitionTests
{
    private static readonly TimeZoneInfo Ljubljana = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");

    private static DateTime Utc(int month, int day, int hour, int minute = 0, int year = 2027) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static IActaAvailabilityCalendar Availability(string json) =>
        CalendarDefinition.Compile(CalendarRoleCode.Availability, json).Availability!;

    private static IActaCalendar Occurrence(string json) => CalendarDefinition.Compile(CalendarRoleCode.Occurrence, json).Calendar!;

    private static CalendarWindow? WindowAt(IActaAvailabilityCalendar c, DateTime at) =>
        c.GetWindowAtOrAfter(new AvailabilityContext(at, Ljubljana));

    private static DateTime? NextAfter(IActaCalendar c, DateTime after) => c.GetNextOccurrence(new CalendarContext(after, Ljubljana));

    private const string OfficeHours = """
        {"kind":"weekly-availability","timeZone":"schedule",
         "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday"],"windows":[["09:00","12:00"],["13:00","17:00"]]}],
         "dateOverrides":{"2027-01-08":[["09:00","12:00"]],"2027-01-11":[]},
         "excludedRanges":[{"from":"2027-01-18","to":"2027-01-19"}]}
        """;

    [Fact]
    public void Weekly_windows_resolve_in_the_schedule_zone_with_overrides_and_exclusions()
    {
        // 2027-01-04 is a Monday; Ljubljana is UTC+1 in January.
        var office = Availability(OfficeHours);

        Assert.Equal(new CalendarWindow(Utc(1, 4, 8), Utc(1, 4, 11)), WindowAt(office, Utc(1, 4, 6)));
        Assert.Equal(new CalendarWindow(Utc(1, 4, 8), Utc(1, 4, 11)), WindowAt(office, Utc(1, 4, 10, 30)));
        Assert.Equal(new CalendarWindow(Utc(1, 4, 12), Utc(1, 4, 16)), WindowAt(office, Utc(1, 4, 11)));
        // Friday the 8th is overridden to the morning, Monday the 11th is closed, the 18th and 19th are excluded.
        Assert.Equal(new CalendarWindow(Utc(1, 8, 8), Utc(1, 8, 11)), WindowAt(office, Utc(1, 8, 7)));
        Assert.Equal(new CalendarWindow(Utc(1, 12, 8), Utc(1, 12, 11)), WindowAt(office, Utc(1, 8, 11)));
        Assert.Equal(new CalendarWindow(Utc(1, 20, 8), Utc(1, 20, 11)), WindowAt(office, Utc(1, 15, 16)));
    }

    [Fact]
    public void An_overnight_window_crosses_midnight_and_yields_to_the_next_dates_override()
    {
        var nights = Availability(
            """
            {"kind":"weekly-availability","timeZone":"UTC",
             "weekly":[{"days":["Monday","Tuesday"],"windows":[["22:00","06:00"]]}],
             "dateOverrides":{"2027-01-06":[]}}
            """
        );

        Assert.Equal(new CalendarWindow(Utc(1, 4, 22), Utc(1, 5, 6)), WindowAt(nights, Utc(1, 5, 3)));
        // Tuesday night's window is cut at Wednesday's closed override.
        Assert.Equal(new CalendarWindow(Utc(1, 5, 22), Utc(1, 6, 0)), WindowAt(nights, Utc(1, 5, 7)));
    }

    [Fact]
    public void A_full_day_window_merges_across_dates_and_an_instant_exclusion_splits_it()
    {
        var always = Availability(
            """
            {"kind":"weekly-availability","timeZone":"UTC",
             "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],"windows":[["00:00","24:00"]]}],
             "excludedRanges":[{"fromUtc":"2027-01-05T10:00:00Z","toUtc":"2027-01-05T11:00:00Z"}]}
            """
        );

        var window = WindowAt(always, Utc(1, 4, 12));
        Assert.NotNull(window);
        Assert.True(window.Value.StartUtc < Utc(1, 4, 0));
        Assert.Equal(Utc(1, 5, 10), window.Value.EndUtc);
        Assert.Equal(Utc(1, 5, 11), WindowAt(always, Utc(1, 5, 10, 30))!.Value.StartUtc);
    }

    [Fact]
    public void Union_intersection_and_subtraction_compose_windows()
    {
        var mornings = """{"kind":"weekly-availability","timeZone":"UTC","weekly":[{"days":["Monday"],"windows":[["08:00","12:00"]]}]}""";
        var late = """{"kind":"weekly-availability","timeZone":"UTC","weekly":[{"days":["Monday"],"windows":[["11:00","15:00"]]}]}""";
        var lunch = """{"kind":"weekly-availability","timeZone":"UTC","weekly":[{"days":["Monday"],"windows":[["12:00","13:00"]]}]}""";

        var union = Availability($$"""{"kind":"union","of":[{{mornings}},{{late}}]}""");
        var both = Availability($$"""{"kind":"intersection","of":[{{mornings}},{{late}}]}""");
        var minus = Availability($$"""{"kind":"subtraction","from":{{late}},"minus":{{lunch}}}""");

        Assert.Equal(new CalendarWindow(Utc(1, 4, 8), Utc(1, 4, 15)), WindowAt(union, Utc(1, 4, 13)));
        Assert.Equal(new CalendarWindow(Utc(1, 4, 11), Utc(1, 4, 12)), WindowAt(both, Utc(1, 4, 0)));
        Assert.Equal(new CalendarWindow(Utc(1, 11, 11), Utc(1, 11, 12)), WindowAt(both, Utc(1, 4, 12)));
        Assert.Equal(new CalendarWindow(Utc(1, 4, 11), Utc(1, 4, 12)), WindowAt(minus, Utc(1, 4, 0)));
        Assert.Equal(new CalendarWindow(Utc(1, 4, 13), Utc(1, 4, 15)), WindowAt(minus, Utc(1, 4, 12, 30)));
    }

    [Fact]
    public void Explicit_instants_resolve_locals_in_the_zone_and_exhaust()
    {
        var fixedDates = Occurrence(
            """{"kind":"explicit-instants","instants":["2027-03-15T16:00","2027-01-15T16:00:00Z","2027-04-15T16:00"]}"""
        );

        Assert.Equal(Utc(1, 15, 16), NextAfter(fixedDates, Utc(1, 1, 0)));
        // March is still UTC+1 on the 15th; April is UTC+2.
        Assert.Equal(Utc(3, 15, 15), NextAfter(fixedDates, Utc(1, 15, 16)));
        Assert.Equal(Utc(4, 15, 14), NextAfter(fixedDates, Utc(3, 15, 15)));
        Assert.Null(NextAfter(fixedDates, Utc(4, 15, 14)));
    }

    [Fact]
    public void Daily_local_times_skip_excluded_dates_and_decide_a_dst_gap_and_fold()
    {
        var daily = Occurrence("""{"kind":"daily-local-time","times":["02:30"],"days":["Sunday"],"excludedDates":["2027-01-10"]}""");

        Assert.Equal(Utc(1, 3, 1, 30), NextAfter(daily, Utc(1, 1, 0)));
        Assert.Equal(Utc(1, 17, 1, 30), NextAfter(daily, Utc(1, 3, 1, 30)));
        // 2027-03-28 springs forward at 02:00 local: 02:30 does not exist and resolves to the instant time resumes, 01:00Z.
        Assert.Equal(Utc(3, 28, 1), NextAfter(daily, Utc(3, 27, 0)));
        // 2027-10-31 falls back at 03:00 local: 02:30 happens twice and resolves to the earlier, 00:30Z.
        Assert.Equal(Utc(10, 31, 0, 30), NextAfter(daily, Utc(10, 30, 0)));
    }

    [Fact]
    public void A_business_day_source_selects_the_nth_eligible_date_of_each_period()
    {
        var lastOfMonth = Occurrence(
            """{"kind":"business-day","timeZone":"UTC","select":"last","of":"month","holidays":["2027-01-29"],"time":"16:00"}"""
        );
        var secondOfWeek = Occurrence("""{"kind":"business-day","timeZone":"UTC","select":"first","of":"week","nth":2,"time":"09:00"}""");

        // January 2027 ends on a Sunday; the 29th is a holiday, so the last business day is Thursday the 28th.
        Assert.Equal(Utc(1, 28, 16), NextAfter(lastOfMonth, Utc(1, 1, 0)));
        Assert.Equal(Utc(2, 26, 16), NextAfter(lastOfMonth, Utc(1, 28, 16)));
        Assert.Equal(Utc(1, 5, 9), NextAfter(secondOfWeek, Utc(1, 4, 0)));
        Assert.Equal(Utc(1, 12, 9), NextAfter(secondOfWeek, Utc(1, 5, 9)));
    }

    [Fact]
    public void At_opening_fires_at_each_window_start_and_a_union_merges_sources()
    {
        var openings = Occurrence($$"""{"kind":"at-opening","availability":{{OfficeHours}}}""");
        var merged = Occurrence(
            """
            {"kind":"union","of":[
              {"kind":"explicit-instants","instants":["2027-01-04T10:00:00Z"]},
              {"kind":"explicit-instants","instants":["2027-01-04T09:00:00Z","2027-01-04T11:00:00Z"]}]}
            """
        );

        Assert.Equal(Utc(1, 4, 8), NextAfter(openings, Utc(1, 2, 0)));
        Assert.Equal(Utc(1, 4, 12), NextAfter(openings, Utc(1, 4, 8)));
        Assert.Equal(Utc(1, 5, 8), NextAfter(openings, Utc(1, 4, 12)));
        Assert.Equal(Utc(1, 4, 9), NextAfter(merged, Utc(1, 4, 0)));
        Assert.Equal(Utc(1, 4, 10), NextAfter(merged, Utc(1, 4, 9)));
    }

    [Fact]
    public void The_canonical_form_and_hash_ignore_whitespace()
    {
        var a = CalendarDefinition.Compile(CalendarRoleCode.Availability, OfficeHours);
        var b = CalendarDefinition.Compile(CalendarRoleCode.Availability, OfficeHours.Replace("\n", "").Replace(" ", ""));

        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal("weekly-availability", a.Kind);
        Assert.DoesNotContain((byte)'\n', a.CanonicalJson.ToArray());
    }

    [Theory]
    [InlineData("""{"kind":"explicit-instants","instants":[]}""", "$.kind: 'explicit-instants' is an occurrence source")]
    [InlineData("""{"kind":"weekly-availability"}""", "$: a weekly-availability needs weekly rules or date overrides.")]
    [InlineData(
        """{"kind":"weekly-availability","weekly":[{"days":["Funday"],"windows":[["09:00","09:00"]]}]}""",
        "$.weekly[0].days[0]: a weekday"
    )]
    [InlineData(
        """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","09:00"]]}]}""",
        "$.weekly[0].windows[0]: a window ends after it starts"
    )]
    [InlineData(
        """{"kind":"weekly-availability","timeZone":"Mars/Olympus","weekly":[{"days":["Monday"],"windows":[["09:00","10:00"]]}]}""",
        "$.timeZone: 'Mars/Olympus'"
    )]
    [InlineData(
        """{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00","10:00"]]}],"colour":"blue"}""",
        "$.colour: not a property"
    )]
    [InlineData("""{"kind":"subtraction","from":{"kind":"union","of":[]},"minus":5}""", "$.from.of: list at least one operand.")]
    [InlineData("""[1,2]""", "$: a definition is an object with a kind.")]
    public void A_bad_availability_definition_names_each_problem_by_its_path(string json, string problem)
    {
        var ex = Assert.Throws<CalendarDefinitionException>(() => CalendarDefinition.Compile(CalendarRoleCode.Availability, json));

        Assert.Contains(ex.Problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"kind":"weekly-availability","weekly":[]}""", "$.kind: 'weekly-availability' is an availability calendar")]
    [InlineData("""{"kind":"explicit-instants","instants":["yesterday"]}""", "$.instants[0]: 'yesterday' is neither")]
    [InlineData("""{"kind":"business-day","select":"middle","of":"month","time":"16:00"}""", "$.select: one of first, last.")]
    [InlineData("""{"kind":"business-day","select":"first","of":"month","nth":0,"time":"16:00"}""", "$.nth: nth is a whole number")]
    [InlineData("""{"kind":"daily-local-time","times":["25:00"]}""", "$.times[0]: a time is HH:mm")]
    [InlineData("""{"kind":"daily-local-time"}""", "$.times: required.")]
    public void A_bad_occurrence_definition_names_each_problem_by_its_path(string json, string problem)
    {
        var ex = Assert.Throws<CalendarDefinitionException>(() => CalendarDefinition.Compile(CalendarRoleCode.Occurrence, json));

        Assert.Contains(ex.Problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }
}
