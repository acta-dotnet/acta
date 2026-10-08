namespace Acta.Runtime;

/// <summary>The consume outcome projection: how many rows the version-CAS delete removed (0 or 1).</summary>
[DbProjection]
internal sealed record ConsumeOutboxSignalRow(long Consumed);
