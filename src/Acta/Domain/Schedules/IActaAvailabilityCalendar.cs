namespace Acta;

/// <summary>
/// The windows in which a schedule's occurrences may fire: office hours, a maintenance blackout's complement, a
/// holiday calendar's open days. A source occurrence outside every window is omitted, never moved: an interval tick in
/// a closed window is dropped, and the next tick inside an open one fires on the grid. An availability calendar opens
/// no time the source does not produce. Same contract as <see cref="IActaCalendar"/>: quick, deterministic, no I/O.
/// </summary>
public interface IActaAvailabilityCalendar
{
    /// <summary>
    /// The window containing <see cref="AvailabilityContext.AtOrAfterUtc"/> or the earliest one after it, never a
    /// window that ends at or before the instant; null when no open time remains. Windows are ordered, disjoint, and
    /// merged when adjacent.
    /// </summary>
    CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context);
}
