namespace Acta.Runtime.Querying;

/// <summary>
/// One page of a keyset list operation. <see cref="Total"/> is the opt-in filter-wide count, null
/// unless requested. Positional order is load-bearing: facades deconstruct via
/// <c>var (rows, total) = await Op.Run(...)</c>.
/// </summary>
internal readonly record struct RowPage<TRow>(IReadOnlyList<TRow> Rows, long? Total);
