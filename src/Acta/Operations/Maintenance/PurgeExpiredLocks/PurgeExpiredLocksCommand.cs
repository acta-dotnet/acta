namespace Acta;

/// <summary>One batch of lock rows, of every namespace, whose expiry is at or before <see cref="CutoffUtc"/>.</summary>
internal sealed record PurgeExpiredLocksCommand(DateTime CutoffUtc, int BatchSize);
