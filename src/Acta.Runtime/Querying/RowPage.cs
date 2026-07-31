namespace Acta.Runtime.Querying;

/// <summary>
/// One page of a keyset list operation: the page <see cref="Rows"/> plus the opt-in filter-wide
/// <see cref="Total"/> count (null unless requested). A named result so list operations never return a
/// tuple; it positional-deconstructs at the facade, so <c>var (rows, total) = await Op.Run(...)</c>
/// keeps reading the same way.
/// </summary>
internal readonly record struct RowPage<TRow>(IReadOnlyList<TRow> Rows, long? Total);
