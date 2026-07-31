namespace Acta.Runtime.Querying;

/// <summary>
/// Paging bounds shared by every <see cref="IJobs"/> list read.
/// </summary>
internal static class JobsQueryLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxTagFilters = TagLimits.MaxFiltersPerQuery;

    /// <summary>
    /// Applies the page-size rules: null defaults, below one throws, above the cap clamps.
    /// </summary>
    public static int NormalizePageSize(int? pageSize)
    {
        if (pageSize is null)
        {
            return DefaultPageSize;
        }

        if (pageSize.Value < 1)
        {
            throw new InvalidQueryException("pageSize must be at least 1.");
        }
        return Math.Min(pageSize.Value, MaxPageSize);
    }
}
