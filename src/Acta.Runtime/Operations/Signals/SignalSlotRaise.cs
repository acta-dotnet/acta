namespace Acta.Runtime;

/// <summary>
/// One slot raise as the SQL takes it, the caller having validated the name and payload: the resolved job,
/// the slot's kind and name, the payload, and the audit input.
/// </summary>
internal sealed record SignalSlotRaise(
    long JobId,
    CheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value,
    JobControlInput Input
);
