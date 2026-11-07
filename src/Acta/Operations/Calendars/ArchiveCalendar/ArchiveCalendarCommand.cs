namespace Acta;

/// <summary>One archive as the facade received it: the family and the current version the caller saw, null for no check.</summary>
internal sealed record ArchiveCalendarCommand(CalendarLookup Calendar, int? ExpectedCurrentVersion, string? ReasonMessage, string? Actor);
