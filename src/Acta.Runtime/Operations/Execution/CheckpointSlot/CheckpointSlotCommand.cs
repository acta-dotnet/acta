namespace Acta.Runtime;

/// <summary>One validated checkpoint-slot call; a write action carries the payload columns.</summary>
internal sealed record CheckpointSlotCommand(
    CheckpointSlotAction Action,
    long JobId,
    CheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value
);
