namespace Acta;

/// <summary>
/// Exact tag predicate used by typed list queries. A null <see cref="Value"/> matches a tag by name
/// regardless of whether it has a value; a non-null value matches case-insensitively, ignoring surrounding
/// whitespace, and otherwise exactly.
/// Multiple filters on a query are combined with AND semantics.
/// </summary>
public sealed record TagFilter(string Name, string? Value = null);
