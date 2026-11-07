namespace Acta;

/// <summary>The durable wait a Suspended Job is blocked on: a signal name, or a timer and its due instant.</summary>
/// <param name="Kind">What the job waits on: a signal, a child latch, or a timer.</param>
/// <param name="Name">The name of the signal, child latch, or timer the job waits on.</param>
/// <param name="DueAtUtc">When the wait times out or the timer is due, in UTC; null for a wait without a bound.</param>
public sealed record ExplainJobWait(CheckpointKindCode Kind, string Name, DateTime? DueAtUtc);
