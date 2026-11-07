namespace Acta;

/// <summary>
/// One open range of an availability calendar: <c>[StartUtc, EndUtc)</c>, both UTC, the end after the start; a null
/// end is open forever.
/// </summary>
/// <param name="StartUtc">When the window opens, in UTC.</param>
/// <param name="EndUtc">When the window closes, in UTC.</param>
public readonly record struct CalendarWindow(DateTime StartUtc, DateTime? EndUtc);
