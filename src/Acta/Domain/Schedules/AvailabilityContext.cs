namespace Acta;

/// <summary>
/// What an availability calendar is asked: the UTC instant to find the window at or after (inclusive, so a firing
/// exactly at an opening is kept), and the schedule's zone for providers whose windows are local.
/// </summary>
public readonly record struct AvailabilityContext(DateTime AtOrAfterUtc, TimeZoneInfo TimeZone);
