namespace Acta.Runtime;

/// <summary>A <c>checkpoints</c> row's stored format id, bytes, and version: a variable, or a pending outbox command whose version feeds the consume CAS.</summary>
[DbProjection]
internal sealed record CheckpointValue(byte ValueFormatId, byte[] Value, int Version);
