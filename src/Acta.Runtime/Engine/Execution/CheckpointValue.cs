namespace Acta.Runtime;

/// <summary>The stored format id, bytes, and version of a variable <c>checkpoints</c> row.</summary>
[DbProjection]
internal sealed record CheckpointValue(byte ValueFormatId, byte[] Value, int Version);
