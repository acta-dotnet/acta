namespace Acta;

/// <summary>
/// One projection variable of a namespace's <c>sys.alerts</c> slot: the cursor or a skip record. A null name is the
/// slot itself with no such variable; no row at all means the namespace has no slot.
/// </summary>
[DbProjection]
internal sealed record GetAlertFloorRow(string? CheckpointName, byte? ValueFormatId, byte[]? Value);
