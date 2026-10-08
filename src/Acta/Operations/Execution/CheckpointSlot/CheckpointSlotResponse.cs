namespace Acta;

/// <summary>
/// One row of <c>checkpoint_slot</c>: the action outcome flag plus the slot value columns (NULL for
/// the write/exists/delete shapes and for a missing slot).
/// </summary>
[DbProjection]
internal sealed record CheckpointSlotResponse(int Found, byte? ValueFormatId, byte[]? Value, int? Version);
