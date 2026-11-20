namespace Acta;

/// <summary>One batch of one namespace's lanes that no <c>jobs_state</c> row references.</summary>
internal sealed record PurgeUnusedLanesCommand(int NamespaceId, int BatchSize);
