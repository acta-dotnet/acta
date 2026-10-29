namespace Acta;

/// <summary>Which slots of the plan applied; the admitter replans the stale rest.</summary>
internal sealed record AdmitFiringsResponse(IReadOnlyList<AdmitFiringsRow> Slots);
