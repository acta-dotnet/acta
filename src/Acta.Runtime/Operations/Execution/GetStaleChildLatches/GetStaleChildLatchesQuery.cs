namespace Acta.Runtime;

/// <summary>The namespace to scan for child latches a terminal child never set.</summary>
internal sealed record GetStaleChildLatchesQuery(int NamespaceId);
