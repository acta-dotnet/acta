namespace Acta;

/// <summary>The durable wait the focused Job is blocked on: a signal, child latch, or timer slot, and its due instant.</summary>
public sealed record GetJobLineageMapWait(CheckpointKindCode Kind, string Name, DateTime? DueAtUtc);
