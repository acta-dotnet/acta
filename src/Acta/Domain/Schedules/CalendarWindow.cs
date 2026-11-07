namespace Acta;

/// <summary>
/// One open range of an availability calendar: from startUtc up to, and not including, endUtc; a null endUtc never closes.
/// </summary>
/// <param name="StartUtc">When the window opens, in UTC.</param>
/// <param name="EndUtc">When the window closes, in UTC; null for a window that never closes.</param>
public readonly record struct CalendarWindow(DateTime StartUtc, DateTime? EndUtc);
