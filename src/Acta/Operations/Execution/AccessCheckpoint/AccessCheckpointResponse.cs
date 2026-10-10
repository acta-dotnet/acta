namespace Acta;

/// <summary>
/// One row of <c>access_checkpoint</c>: the action outcome flag plus the slot value columns (NULL for
/// the write/exists/delete shapes and for a missing slot).
/// </summary>
[DbProjection]
internal sealed record AccessCheckpointResponse(int Found, byte? ValueFormatId, byte[]? Value, int? Version);
