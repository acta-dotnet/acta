namespace Acta;

/// <summary>The parent whose children to find, and the names to find them by.</summary>
internal sealed record ListChildJobsByNameQuery(long ParentJobId, IReadOnlyList<string> Names);
