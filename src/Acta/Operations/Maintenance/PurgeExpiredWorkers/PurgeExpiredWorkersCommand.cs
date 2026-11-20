namespace Acta;

/// <summary>One batch of one namespace's stopped and dead workers whose last heartbeat and last event are past the cutoff.</summary>
internal sealed record PurgeExpiredWorkersCommand(int NamespaceId, DateTime CutoffUtc, int BatchSize);
