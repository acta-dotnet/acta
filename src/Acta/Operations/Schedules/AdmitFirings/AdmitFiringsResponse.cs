namespace Acta;

/// <summary>Which groups of the plan applied; the admitter replans the stale rest.</summary>
internal sealed record AdmitFiringsResponse(IReadOnlyList<AdmitFiringsRow> Groups);
