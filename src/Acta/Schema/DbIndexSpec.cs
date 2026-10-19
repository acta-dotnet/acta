namespace Acta;

/// <summary>
/// One index declared on an entity; unique (<c>ux_*</c>) and non-unique (<c>ix_*</c>) declarations collapse onto this
/// shape with <see cref="IsUnique"/> as the discriminator. <see cref="Columns"/> are the key columns in declaration
/// order; <see cref="Descending"/> and <see cref="Filter"/> are null when absent;
/// <see cref="Usage"/> names the access pattern the index serves, for documentation only.
/// </summary>
internal sealed record DbIndexSpec(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string>? Descending,
    string? Filter,
    string Usage,
    bool IsUnique
);
