namespace Acta;

/// <summary>One slot of the applied plan: whether its group applied, or was stale and replans.</summary>
[DbProjection(ByName = true)]
internal sealed record AdmitFiringsRow(long JobId, bool Applied);
