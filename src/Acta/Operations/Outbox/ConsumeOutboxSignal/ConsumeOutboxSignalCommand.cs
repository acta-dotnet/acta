namespace Acta;

/// <summary>
/// The applied command row to delete, matched by the stored value the applying tick read, and the evidence
/// event its deletion writes against the slot job: the operator, and a reason carrying their justification
/// plus the applied row ids, so proof of the action outlives the source rows it may have deleted.
/// </summary>
internal sealed record ConsumeOutboxSignalCommand(
    long JobId,
    string Name,
    byte[] Value,
    EventCode EventCode,
    string? Actor,
    string? ReasonMessage
);
