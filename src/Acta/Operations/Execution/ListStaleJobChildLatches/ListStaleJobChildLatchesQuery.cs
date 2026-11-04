namespace Acta;

/// <summary>The namespace to scan for child latches a terminal child never set.</summary>
internal sealed record ListStaleJobChildLatchesQuery(int NamespaceId);
