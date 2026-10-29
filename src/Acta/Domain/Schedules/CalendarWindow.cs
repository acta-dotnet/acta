namespace Acta;

/// <summary>
/// One open range of an availability calendar: <c>[StartUtc, EndUtc)</c>, both UTC, the end after the start; a null
/// end is open forever.
/// </summary>
public readonly record struct CalendarWindow(DateTime StartUtc, DateTime? EndUtc);
