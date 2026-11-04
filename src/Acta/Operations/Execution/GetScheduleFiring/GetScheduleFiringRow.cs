namespace Acta;

[DbProjection(ByName = true)]
internal sealed record GetScheduleFiringRow(byte[]? Value, bool FirstAttempt);
