namespace Acta;

/// <summary>
/// The primary-key declaration on an entity. <see cref="Columns"/> lists every column in
/// declaration order; <see cref="Manual"/> suppresses provider-native identity emission on an
/// otherwise-eligible single-column integer PK.
/// </summary>
internal sealed record DbPrimaryKeySpec(string Name, IReadOnlyList<string> Columns, bool Manual = false)
{
    /// <summary>
    /// The composite key's provider-allocated column from <see cref="DbPrimaryKeyAttribute.Identity"/>; null when the
    /// key declares none.
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="DbPrimaryKeyAttribute.OptimizeForSequentialKey"/> opts the
    /// clustered PK into SQL Server <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; no-op elsewhere.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}
