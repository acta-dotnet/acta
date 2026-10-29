namespace Acta;

/// <summary>
/// The <c>union</c> of availability calendars: open whenever any operand is. The answered window is canonical, grown
/// backwards and forwards over every operand's touching windows, so the operands' own merging is not relied on.
/// </summary>
internal sealed class UnionAvailabilityCalendar(IReadOnlyList<IActaAvailabilityCalendar> operands) : IActaAvailabilityCalendar
{
    public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
    {
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

        var start = first.StartUtc;
        var end = first.EndUtc;
        for (var (grown, hops) = (true, 0); grown && start > DateTime.MinValue && hops < AvailabilityAlgebra.MaxHops; hops++)
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

        for (var hops = 0; end is { } e && hops < AvailabilityAlgebra.MaxHops; hops++)
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

/// <summary>What the composed availability kinds share: the bound on how many windows one answer may step across.</summary>
internal static class AvailabilityAlgebra
{
    public const int MaxHops = 10_000;

    public static InvalidOperationException TooManyHops(string kind, DateTime at) =>
        new($"The {kind} availability stepped across {MaxHops} windows after {at:O} without an answer; the operands do not open together.");
}
