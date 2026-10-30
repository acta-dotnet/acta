using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// A calendar source and an availability filter through <see cref="NextOccurrenceCalculator"/>: a calendar's instants
/// in order and its exhaustion; an availability calendar omitting the cron ticks, interval ticks, and calendar instants
/// outside its windows without shifting them, keeping one exactly at an opening; the provider contract enforced (UTC,
/// advancing, whole milliseconds, a window that contains or follows the instant); the registry's one implementation
/// per name; the fingerprint following the names; and registration keeping nothing but the names.
/// </summary>
public sealed class CalendarSourceTests
{
    private static DateTime Utc(int h, int m, int s = 0) => new(2024, 1, 1, h, m, s, DateTimeKind.Utc);

    private static readonly ScheduleTiming Fixed = new("fixed", ScheduleExpressionKindCode.Calendar, "UTC", null);

    private static ScheduleSource Available(string expression, ScheduleExpressionKindCode kind, DateTime? anchor, Windows windows) =>
        new(new ScheduleTiming(expression, kind, "UTC", anchor, "office"), null, windows);

    [Fact]
    public void A_calendar_yields_its_instants_in_order_and_then_exhausts()
    {
        var source = new ScheduleSource(Fixed, new FixedCalendar(Utc(9, 0), Utc(13, 0)), null);

        Assert.Equal(Utc(9, 0), NextOccurrenceCalculator.Next(source, Utc(8, 0)));
        Assert.Equal(Utc(13, 0), NextOccurrenceCalculator.Next(source, Utc(9, 0)));
        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(13, 0)));
        Assert.Equal([Utc(9, 0), Utc(13, 0)], NextOccurrenceCalculator.Walk(source, Utc(0, 0), 10));
    }

    [Fact]
    public void An_unregistered_calendar_is_an_error_not_exhaustion()
    {
        var ex = Assert.Throws<CalendarUnavailableException>(() =>
            NextOccurrenceCalculator.Next(new ScheduleSource(Fixed, null, null), Utc(8, 0))
        );

        Assert.Contains("'fixed' is not declared in this process", ex.Message);
    }

    [Theory]
    [InlineData("same", "must advance")]
    [InlineData("local", "UTC instant")]
    [InlineData("ticks", "finer than the millisecond")]
    [InlineData("throws", "threw while asked")]
    public void A_calendar_outside_its_contract_is_an_error(string shape, string message)
    {
        var calendar = new Lambda(c =>
            shape switch
            {
                "same" => c.AfterUtc,
                "local" => DateTime.SpecifyKind(Utc(9, 0), DateTimeKind.Local),
                "ticks" => Utc(9, 0).AddTicks(1),
                _ => throw new NotSupportedException("no data"),
            }
        );

        var ex = Assert.Throws<InvalidOperationException>(() =>
            NextOccurrenceCalculator.Next(new ScheduleSource(Fixed, calendar, null), Utc(8, 0))
        );

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void An_availability_window_omits_cron_ticks_outside_it_and_keeps_the_one_at_the_opening()
    {
        var office = new Windows((Utc(9, 0), Utc(12, 0)), (Utc(13, 0), Utc(17, 0)));
        var source = Available("*/30 * * * *", ScheduleExpressionKindCode.Cron, null, office);

        Assert.Equal(Utc(9, 0), NextOccurrenceCalculator.Next(source, Utc(8, 10)));
        Assert.Equal(Utc(13, 0), NextOccurrenceCalculator.Next(source, Utc(11, 30)));
        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(16, 30)));
    }

    [Fact]
    public void An_availability_window_omits_interval_ticks_and_never_shifts_them()
    {
        // Ticks every 50 minutes from 08:00: 08:50 is closed, 09:40 open, 10:30 closed, 11:20 open; no tick moves to an opening.
        var office = new Windows((Utc(9, 0), Utc(10, 0)), (Utc(11, 0), Utc(12, 0)));
        var source = Available("PT50M", ScheduleExpressionKindCode.Interval, Utc(8, 0), office);

        Assert.Equal(Utc(9, 40), NextOccurrenceCalculator.FirstAfter(source, Utc(8, 0), Utc(8, 0)));
        Assert.Equal(Utc(11, 20), NextOccurrenceCalculator.Next(source, Utc(9, 40)));
        Assert.Null(NextOccurrenceCalculator.Next(source, Utc(11, 20)));
    }

    [Fact]
    public void An_availability_window_filters_a_calendar_source()
    {
        var timing = Fixed with { AvailabilityCalendarName = "office" };
        var source = new ScheduleSource(timing, new FixedCalendar(Utc(9, 0), Utc(12, 30), Utc(14, 0)), new Windows((Utc(13, 0), null)));

        Assert.Equal(Utc(14, 0), NextOccurrenceCalculator.Next(source, Utc(0, 0)));
    }

    [Fact]
    public void A_window_that_ends_before_the_instant_is_a_provider_error()
    {
        var stale = new Lambda(_ => Utc(9, 0), _ => new CalendarWindow(Utc(8, 0), Utc(8, 30)));
        var timing = Fixed with { AvailabilityCalendarName = "office" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            NextOccurrenceCalculator.Next(new ScheduleSource(timing, stale, stale), Utc(0, 0))
        );

        Assert.Contains("must contain the instant or follow it", ex.Message);
    }

    [Fact]
    public void The_registry_holds_one_implementation_per_name()
    {
        var registry = new CalendarRegistry();
        var one = new FixedCalendar(Utc(9, 0));
        var two = new FixedCalendar(Utc(10, 0));

        registry.Add("Fixed", one);
        registry.Add("fixed", one);
        registry.Add("other", two);
        Assert.Throws<InvalidOperationException>(() => registry.Add("fixed", two));
        Assert.Throws<ArgumentException>(() => registry.Add("sys.fixed", two));

        Assert.Same(one, registry.Source(Fixed).Calendar);
        Assert.Null(registry.Source(Fixed with { AvailabilityCalendarName = "fixed" }).Availability);
        Assert.Same(two, registry.Source(Fixed with { Expression = "other" }).Calendar);
        Assert.Null(registry.Source(Fixed with { Expression = "missing" }).Calendar);
    }

    [Fact]
    public void The_fingerprint_follows_the_names_and_the_zone_an_availability_window_resolves_in()
    {
        var a = TimingFingerprint.Compute(Fixed);

        Assert.Equal(a, TimingFingerprint.Compute(Fixed with { }));
        Assert.NotEqual(a, TimingFingerprint.Compute(Fixed with { Expression = "other" }));
        Assert.NotEqual(a, TimingFingerprint.Compute(Fixed with { AvailabilityCalendarName = "office" }));
        var bare = new ScheduleTiming("PT5M", ScheduleExpressionKindCode.Interval, "UTC", Utc(9, 0));
        Assert.Equal(a.Length, TimingFingerprint.Compute(bare).Length);
        Assert.Equal(TimingFingerprint.Compute(bare), TimingFingerprint.Compute(bare with { TimeZoneId = "Europe/Ljubljana" }));
        var windowed = bare with { AvailabilityCalendarName = "office" };
        Assert.NotEqual(TimingFingerprint.Compute(windowed), TimingFingerprint.Compute(windowed with { TimeZoneId = "Europe/Ljubljana" }));
    }

    [Fact]
    public void A_registration_plan_refuses_a_name_nothing_resolves_and_keeps_a_cursor_whose_name_still_matches()
    {
        var declared = new ScheduleDescriptor(
            "settle",
            "daily",
            "spec.days",
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Calendar,
            null,
            []
        );
        var none = new Dictionary<string, StoredScheduleState>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ScheduleRegistrationPlan.Plan([declared], none, Utc(8, 0), CalendarRegistry.Empty.Source)
        );

        Assert.Contains("Schedule 'daily' of job 'settle'", ex.Message);
        Assert.Contains("'spec.days' is not declared in this process and has no published version", ex.Message);

        var planned = Assert.Single(
            ScheduleRegistrationPlan.Plan([declared], none, Utc(8, 0), t => new ScheduleSource(t, new FixedCalendar(Utc(9, 0)), null))
        );
        Assert.Equal(Utc(9, 0), planned.NextOccurrenceAtUtc);

        // The fingerprint holds the name only, so a name that now resolves to another calendar keeps the stored cursor;
        // the admitter's cursor heals an occurrence the new calendar does not produce.
        var stored = new Dictionary<string, StoredScheduleState>
        {
            ["daily"] = new(
                1,
                "daily",
                Utc(13, 0),
                ExpressionKind: ScheduleExpressionKindCode.Calendar,
                TimingFingerprint: planned.TimingFingerprint
            ),
        };
        var moved = Assert.Single(
            ScheduleRegistrationPlan.Plan([declared], stored, Utc(8, 0), t => new ScheduleSource(t, new FixedCalendar(Utc(10, 0)), null))
        );
        Assert.Equal(Utc(13, 0), moved.NextOccurrenceAtUtc);
    }

    [Fact]
    public void A_declared_calendar_is_created_through_the_services_under_the_roles_its_class_implements()
    {
        var services = new ServiceCollection().AddSingleton(new Opening(Utc(9, 0))).BuildServiceProvider();

        var registry = new CalendarRegistry().AddDeclared(services, [new CalendarDescriptor("office", typeof(DeclaredOffice), true, true)]);

        var timing = new ScheduleTiming("office", ScheduleExpressionKindCode.Calendar, "UTC", null);
        Assert.NotNull(registry.Source(timing with { AvailabilityCalendarName = "office" }).Availability);
        Assert.Equal(Utc(9, 0), NextOccurrenceCalculator.Next(registry.Source(timing), Utc(8, 0)));
    }

    private sealed record Opening(DateTime AtUtc);

    /// <summary>A declared calendar with a constructor dependency, opening once at the injected instant and open from then on.</summary>
    private sealed class DeclaredOffice(Opening opening) : IActaCalendar, IActaAvailabilityCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context) => context.AfterUtc < opening.AtUtc ? opening.AtUtc : null;

        public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context) => new CalendarWindow(opening.AtUtc, null);
    }

    private sealed class FixedCalendar(params DateTime[] instants) : IActaCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context)
        {
            foreach (var instant in instants)
            {
                if (instant > context.AfterUtc)
                {
                    return instant;
                }
            }

            return null;
        }
    }

    private sealed class Lambda(Func<CalendarContext, DateTime?> next, Func<AvailabilityContext, CalendarWindow?>? window = null)
        : IActaCalendar,
            IActaAvailabilityCalendar
    {
        public DateTime? GetNextOccurrence(CalendarContext context) => next(context);

        public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context) => window!(context);
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
