namespace Acta;

/// <summary>
/// One page of a list, keyset-paged: pass <see cref="NextCursor"/> back as the list query's <c>Cursor</c> to read the
/// next page. Every list operation answers with its own <c>ListXResponse</c> implementing this interface.
/// </summary>
/// <typeparam name="T">The list's element.</typeparam>
public interface IPagedResponse<out T>
{
    /// <summary>The page's elements, at most <see cref="PageSize"/> of them.</summary>
    IReadOnlyList<T> Items { get; }

    /// <summary>Opaque cursor for the next page, or null on the final page.</summary>
    string? NextCursor { get; }

    /// <summary>Whether at least one more element exists beyond this page.</summary>
    bool HasMore { get; }

    /// <summary>The effective page size after defaulting and clamping.</summary>
    int PageSize { get; }

    /// <summary>The filter-wide count, populated only when the query set <c>IncludeTotal</c>.</summary>
    long? TotalCount { get; }
}
