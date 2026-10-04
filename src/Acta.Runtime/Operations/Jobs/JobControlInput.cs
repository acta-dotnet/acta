namespace Acta.Runtime;

/// <summary>
/// The actor, reason, and optional expected version stamped on a job control transition. A null
/// <c>ExpectedVersion</c> writes unconditionally.
/// </summary>
internal sealed record JobControlInput(
    JobControlActor Actor,
    JobEventReasonCode ReasonCode,
    string? ReasonMessage,
    int? ExpectedVersion = null
);
