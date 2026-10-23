namespace Acta;

/// <summary>The consume outcome projection: how many rows the delete removed (0 or 1).</summary>
[DbProjection]
internal sealed record ConsumeOutboxSignalRow(long Consumed);
