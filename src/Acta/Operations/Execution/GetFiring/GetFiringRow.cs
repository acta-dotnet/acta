namespace Acta;

[DbProjection(ByName = true)]
internal sealed record GetFiringRow(byte[]? Value, bool FirstAttempt);
