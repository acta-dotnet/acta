namespace Acta;

/// <summary>
/// One index declared on an entity; unique (<c>ux_*</c>) and non-unique (<c>ix_*</c>) declarations collapse onto this
/// shape with <see cref="IsUnique"/> as the discriminator. <see cref="Columns"/> are the key columns in declaration
/// order; <see cref="Descending"/>, <see cref="Include"/>, and <see cref="Filter"/> are null when absent;
/// <see cref="Usage"/> names the access pattern the index serves, for documentation only.
/// </summary>
internal sealed record DbIndexSpec(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string>? Descending,
    IReadOnlyList<string>? Include,
    string? Filter,
    string Usage,
    bool IsUnique
)
{
    /// <summary>
    /// <c>true</c> when <see cref="DbIndexAttribute.OptimizeForSequentialKey"/> opts the index into SQL Server
    /// <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; no-op elsewhere.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}
