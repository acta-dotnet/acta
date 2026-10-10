namespace Acta;

/// <summary>One validated <c>access_checkpoint</c> call; a write action carries the payload columns.</summary>
internal sealed record AccessCheckpointCommand(
    AccessCheckpointAction Action,
    long JobId,
    CheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value
);
