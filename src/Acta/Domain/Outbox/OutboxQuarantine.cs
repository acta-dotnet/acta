namespace Acta;

/// <summary>Per-row quarantine outcome: the row's own consumed failure count and bounded error.</summary>
internal readonly record struct OutboxQuarantine(Guid JobRef, int FailureCount, string? LastError);
