namespace Acta;

/// <summary>One version of a family with its definition, as a publication or a preview reads it, and the namespace the family lives in.</summary>
[DbProjection(ByName = true)]
internal sealed record FamilyVersion(
    int NamespaceId,
    int CalendarId,
    int Version,
    CalendarRoleCode Role,
    CalendarStatusCode Status,
    byte DefinitionFormatId,
    ReadOnlyMemory<byte> Definition
);
