namespace Acta;

/// <summary>The durable wait a Suspended Job is blocked on: a signal name, a child, or a timer and its due instant.</summary>
/// <param name="Kind">What the job waits on: a signal, a child latch, or a timer.</param>
/// <param name="Name">The name of the signal, child latch, or timer the job waits on.</param>
/// <param name="DueAtUtc">When the wait times out or the timer is due, in UTC; null for a wait without a bound.</param>
/// <param name="ChildJobRef">The ref of the child a child-latch wait waits on; null for any other wait, or once the child is purged.</param>
/// <param name="ChildName">The name the parent gave that child, the call's name or {map}/{key}; null where childJobRef is, and for a
/// child the parent gave no name.</param>
public sealed record ExplainJobWait(
    CheckpointKindCode Kind,
    string Name,
    DateTime? DueAtUtc,
    JobRef? ChildJobRef = null,
    string? ChildName = null
);
