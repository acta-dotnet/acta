namespace Acta;

/// <summary>Per-row quarantine outcome: the row's own consumed failure count and bounded error.</summary>
internal readonly record struct OutboxQuarantine(Guid OutboxId, int FailureCount, string? LastError);
