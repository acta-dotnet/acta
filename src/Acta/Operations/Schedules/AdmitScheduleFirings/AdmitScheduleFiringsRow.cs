namespace Acta;

/// <summary>One group of the applied plan, by its ordinal: whether it applied, or was stale and replans.</summary>
[DbProjection(ByName = true)]
internal sealed record AdmitScheduleFiringsRow(int Ordinal, bool Applied);
