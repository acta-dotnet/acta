namespace Acta;

/// <summary>
/// One raise as the SQL takes it, the caller having validated the name and payload: the resolved job,
/// the checkpoint's kind and name, the payload, and the audit input. A child latch also names the child and the
/// status it reports, so the raise writes nothing once the child holds another.
/// </summary>
internal sealed record RaiseJobSignalCheckpoint(
    long JobId,
    CheckpointKindCode Kind,
    string Name,
    byte ValueFormatId,
    byte[]? Value,
    JobControlInput Input,
    long? ChildJobId = null,
    JobStatusCode? ChildStatus = null
);
