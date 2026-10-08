namespace Acta.Runtime;

/// <summary>
/// The paging every list operation shares. A list names itself, its sort order, the kinds of its sort keys,
/// and the filters that shape it, so a cursor it hands out resumes only that list under those filters. The
/// store reads <see cref="Fetch"/> rows after <see cref="After"/>, one past the page, so a full page knows
/// another follows.
/// </summary>
internal sealed class Paging
{
    private readonly string _list;
    private readonly string _order;
    private readonly string _filterHash;

    private Paging(string list, string order, string filterHash, int pageSize, object[]? after)
    {
        _list = list;
        _order = order;
        _filterHash = filterHash;
        PageSize = pageSize;
        After = after;
    }

    public int PageSize { get; }

    /// <summary>The rows to read: one past the page.</summary>
    public int Fetch => PageSize + 1;

    /// <summary>The sort keys of the previous page's last row, in sort order; null on the first page.</summary>
    public object[]? After { get; }

    public static Paging Start(
        string list,
        string order,
        ReadOnlySpan<CursorKeyKind> keys,
        string? cursor,
        int? pageSize,
        ReadOnlySpan<(string Key, string? Value)> filters
    )
    {
        var filterHash = QueryFilterHash.Compute(filters);
        var after = cursor is null ? null : PageCursorCodec.Decode(cursor, list, order, filterHash, keys);
        return new Paging(list, order, filterHash, JobsQueryLimits.NormalizePageSize(pageSize), after);
    }

    /// <summary>
    /// Cuts the rows read to the page and encodes the cursor after its last row: the members of the list's
    /// <see cref="IPagedResponse{T}"/>, which the handler builds its own response from.
    /// </summary>
    public (IReadOnlyList<T> Items, string? NextCursor, bool HasMore, int PageSize, long? TotalCount) Page<T>(
        IReadOnlyList<T> rows,
        long? total,
        Func<T, object[]> keys
    )
    {
        var hasMore = rows.Count > PageSize;
        var items = hasMore ? rows.Take(PageSize).ToList() : rows;
        var next = hasMore ? PageCursorCodec.Encode(_list, _order, _filterHash, keys(items[^1])) : null;
        return (items, next, hasMore, PageSize, total);
    }
}
