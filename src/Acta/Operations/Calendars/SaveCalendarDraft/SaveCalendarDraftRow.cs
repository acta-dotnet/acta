namespace Acta;

/// <summary>
/// What the draft routine answers: the action, the version saved or the family's latest, the saved content hash, the
/// family's role, and on a name clash the namespace holding the family the name already belongs to.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record SaveCalendarDraftRow(
    ControlAction Action,
    int? Version,
    string? ContentHash,
    CalendarRoleCode? FamilyRole,
    string? ClashNamespace
);
