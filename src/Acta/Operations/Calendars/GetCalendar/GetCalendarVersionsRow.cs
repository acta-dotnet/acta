namespace Acta;

/// <summary>One version of the family as GetCalendarVersions reads it, with the family's namespace id and role.</summary>
[DbProjection(ByName = true)]
internal sealed record GetCalendarVersionsRow(
    int NamespaceId,
    CalendarRoleCode Role,
    int Version,
    CalendarStatusCode Status,
    string ContentHash,
    DateTime CreatedAtUtc,
    DateTime? PublishedAtUtc,
    DateTime ModifiedAtUtc
);
