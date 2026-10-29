namespace Acta;

/// <summary>
/// The <c>subtraction</c> of one availability calendar from another: open while <c>from</c> is and <c>minus</c> is
/// not. Each window of <c>from</c> is cut by the windows of <c>minus</c> that overlap it, and the first remaining
/// piece ending after the asked instant is the answer.
/// </summary>
internal sealed class SubtractionAvailabilityCalendar(IActaAvailabilityCalendar from, IActaAvailabilityCalendar minus)
    : IActaAvailabilityCalendar
{
    public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
    {
        var at = context.AtOrAfterUtc;
        for (var hops = 0; hops < AvailabilityAlgebra.MaxHops; hops++)
        {
            if (from.GetWindowAtOrAfter(new AvailabilityContext(at, context.TimeZone)) is not { } window)
            {
                return null;
            }

            var start = window.StartUtc;
            var end = window.EndUtc;
            while (hops++ < AvailabilityAlgebra.MaxHops)
            {
                var cut = minus.GetWindowAtOrAfter(new AvailabilityContext(start, context.TimeZone));
                if (cut is not { } c || (end is { } e && c.StartUtc >= e))
                {
                    // Nothing more is cut from this window.
                    if (end is null || end > at)
                    {
                        return new CalendarWindow(start, end);
                    }

                    break;
                }

                if (c.StartUtc > start)
                {
                    if (c.StartUtc > at)
                    {
                        return new CalendarWindow(start, c.StartUtc);
                    }
                }

                // The cut covers the piece's start: the piece resumes where the cut ends, if the window lasts that long.
                if (c.EndUtc is not { } resume)
                {
                    return null;
                }

                start = resume;
                if (end is { } windowEnd && start >= windowEnd)
                {
                    break;
                }
            }

            if (end is not { } next)
            {
                return null;
            }

            at = next;
        }

        throw AvailabilityAlgebra.TooManyHops("subtraction", context.AtOrAfterUtc);
    }
}
