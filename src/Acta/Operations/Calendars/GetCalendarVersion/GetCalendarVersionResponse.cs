namespace Acta;

/// <summary>One version of a family: its role, its status, and its definition.</summary>
internal sealed record GetCalendarVersionResponse(CalendarRoleCode Role, CalendarStatusCode Status, ReadOnlyMemory<byte> Definition);
