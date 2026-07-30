namespace Acta;

/// <summary>
/// One page of a keyset-paginated list read.
/// </summary>
/// <param name="Items">The page rows, at most <paramref name="PageSize"/> of them.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null on the final page.</param>
/// <param name="HasMore">Whether at least one more row exists beyond this page.</param>
/// <param name="PageSize">The effective page size after defaulting and clamping.</param>
/// <param name="TotalCount">Filter-wide row count, populated only when the query set IncludeTotal.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int PageSize, long? TotalCount);
