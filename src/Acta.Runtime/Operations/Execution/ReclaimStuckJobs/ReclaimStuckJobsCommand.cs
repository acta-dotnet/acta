namespace Acta.Runtime;

/// <summary>The namespace whose lapsed leases to reclaim.</summary>
internal sealed record ReclaimStuckJobsCommand(int NamespaceId);
