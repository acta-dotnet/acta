namespace Acta;

/// <summary>One version of a family: its role, its status, and its definition with the definition's payload format.</summary>
internal sealed record ResolveCalendarVersionResponse(
    CalendarRoleCode Role,
    CalendarStatusCode Status,
    byte DefinitionFormatId,
    ReadOnlyMemory<byte> Definition
);
