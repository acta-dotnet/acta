namespace Acta;

/// <summary>The namespace's current calendars by name, one per family that has a Published version.</summary>
internal sealed record GetCurrentCalendarsResponse(IReadOnlyList<GetCurrentCalendarsItem> Calendars);
