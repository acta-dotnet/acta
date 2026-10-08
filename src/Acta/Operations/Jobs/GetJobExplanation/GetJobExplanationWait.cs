namespace Acta;

/// <summary>The durable wait a Suspended Job is blocked on: a signal name, or a timer slot and its due instant.</summary>
public sealed record GetJobExplanationWait(CheckpointKindCode Kind, string Name, DateTime? DueAtUtc);
