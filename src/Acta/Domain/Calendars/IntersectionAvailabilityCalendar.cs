namespace Acta;

/// <summary>
/// The <c>intersection</c> of availability calendars: open only while every operand is. The overlap of the operands'
/// windows at the instant is the answer; where they do not overlap, the seek moves to the earliest end and tries again.
/// </summary>
internal sealed class IntersectionAvailabilityCalendar(IReadOnlyList<IActaAvailabilityCalendar> operands) : IActaAvailabilityCalendar
{
    public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
    {
        var at = context.AtOrAfterUtc;
        for (var hops = 0; hops < AvailabilityAlgebra.MaxHops; hops++)
        {
            var start = DateTime.MinValue;
            DateTime? end = null;
            foreach (var operand in operands)
            {
                if (operand.GetWindowAtOrAfter(new AvailabilityContext(at, context.TimeZone)) is not { } w)
                {
                    return null;
                }

                start = w.StartUtc > start ? w.StartUtc : start;
                end = end is null || (w.EndUtc is { } we && we < end) ? w.EndUtc : end;
            }

            if (end is null || start < end)
            {
                return new CalendarWindow(start, end);
            }

            at = end.Value;
        }

        throw AvailabilityAlgebra.TooManyHops("intersection", context.AtOrAfterUtc);
    }
}
