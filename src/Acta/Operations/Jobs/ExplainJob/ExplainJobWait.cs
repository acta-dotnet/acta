namespace Acta;

/// <summary>The durable wait a Suspended Job is blocked on: a signal name, a child, or a timer and its due instant.</summary>
/// <param name="Kind">What the job waits on: a signal, a child, or a timer.</param>
/// <param name="Name">The name of the signal, child, or timer the job waits on, as the handler named it; for a child the
/// parent gave no name, or one since purged, the wait's own name, sys.child.{id}.</param>
/// <param name="DueAtUtc">When the wait times out or the timer is due, in UTC; null for a wait without a bound.</param>
/// <param name="ChildJobRef">The ref of the child a child wait waits on; null for any other wait, or once the child is purged.</param>
/// <param name="ChildName">The name the parent gave that child, the call's name or {map}/{key}; null where childJobRef is, and for a
/// child the parent gave no name.</param>
public sealed record ExplainJobWait(
    CheckpointKindCode Kind,
    string Name,
    DateTime? DueAtUtc,
    JobRef? ChildJobRef = null,
    string? ChildName = null
);
