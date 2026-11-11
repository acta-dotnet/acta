namespace Acta;

/// <summary>
/// The <c>union</c> of availability calendars: open whenever any operand is. The answered window is grown backwards and
/// forwards over every operand's touching windows, so the operands' own merging is not relied on: forwards up to
/// <see cref="AvailabilityAlgebra.MaxAhead"/> past the asked instant, so a run that ends is answered whole and its end is
/// its own, and backwards up to <see cref="AvailabilityAlgebra.MaxBehind"/> before it. An operand open without a break,
/// as an always-open week is, would otherwise be followed to the ends of the calendar.
/// </summary>
internal sealed class UnionAvailabilityCalendar(IReadOnlyList<IActaAvailabilityCalendar> operands) : IActaAvailabilityCalendar
{
    public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
    {
        var asked = context.AtOrAfterUtc;
        var reach = asked > DateTime.MinValue + AvailabilityAlgebra.MaxBehind ? asked - AvailabilityAlgebra.MaxBehind : DateTime.MinValue;
        var horizon = asked < DateTime.MaxValue - AvailabilityAlgebra.MaxAhead ? asked + AvailabilityAlgebra.MaxAhead : DateTime.MaxValue;
        CalendarWindow? earliest = null;
        foreach (var operand in operands)
        {
            if (operand.GetWindowAtOrAfter(context) is { } w && (earliest is null || w.StartUtc < earliest.Value.StartUtc))
            {
                earliest = w;
            }
        }

        if (earliest is not { } first)
        {
            return null;
        }

        // A run of more touching windows than the bounds is answered as far as it was followed: that much is open time,
        // and the next question continues from its end. Intersection and subtraction have no answer to give at the
        // bound, so they throw there.
        var start = first.StartUtc;
        var end = first.EndUtc;
        for (var (grown, hops) = (true, 0); grown && start > reach && hops < AvailabilityAlgebra.MaxHops; hops++)
        {
            grown = false;
            var before = new AvailabilityContext(start.AddTicks(-1), context.TimeZone);
            foreach (var operand in operands)
            {
                if (operand.GetWindowAtOrAfter(before) is { } w && w.StartUtc < start && w.EndUtc >= start)
                {
                    start = w.StartUtc;
                    grown = true;
                }
            }
        }

        for (var hops = 0; end is { } e && e < horizon && hops < AvailabilityAlgebra.MaxHops; hops++)
        {
            var grown = false;
            var at = new AvailabilityContext(e, context.TimeZone);
            foreach (var operand in operands)
            {
                if (operand.GetWindowAtOrAfter(at) is { } w && w.StartUtc <= e && (w.EndUtc is null || w.EndUtc > e))
                {
                    end = w.EndUtc;
                    grown = true;
                    break;
                }
            }

            if (!grown)
            {
                break;
            }
        }

        return new CalendarWindow(start, end);
    }
}

/// <summary>What the composed availability kinds share: the bounds on how far one answer may step.</summary>
internal static class AvailabilityAlgebra
{
    public const int MaxHops = 10_000;

    /// <summary>
    /// How far past the asked instant a union follows touching windows: as far as a weekly calendar scans, beyond which
    /// a run cannot be told from an endless one.
    /// </summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(WeeklyAvailabilityCalendar.MaxScanDays);

    /// <summary>
    /// How far before the asked instant a union follows touching windows: a month. A caller reads only whether the
    /// instant is inside, so the start goes back no further than a reader of the window needs.
    /// </summary>
    public static readonly TimeSpan MaxBehind = TimeSpan.FromDays(31);

    public static InvalidOperationException TooManyHops(string kind, DateTime at) =>
        new($"The {kind} availability stepped across {MaxHops} windows after {at:O} without an answer; the operands do not open together.");
}
