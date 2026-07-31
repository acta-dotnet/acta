namespace Acta.Runtime.Modules.Execution.Checkpoints;

/// <summary>The stored format id, bytes, and version of a variable <c>checkpoints</c> row.</summary>
internal sealed record CheckpointValue(byte ValueFormatId, byte[] Value, int Version);
