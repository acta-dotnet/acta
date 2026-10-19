namespace Acta;

/// <summary>
/// Scalar parameter descriptor for SQL-only parameters (not bound to a column), declared by hand, with a width only on a
/// string. <see cref="ActaSchema.Sql"/> exposes these by name; provider code calls
/// <c>DbParams.For</c> to materialize the bound <c>DbParameterSpec</c>. Enum-backed parameters carry their physical
/// integer <see cref="DbKind"/> (<see cref="DbKind.Byte"/> / <see cref="DbKind.Int16"/> / <see cref="DbKind.Int32"/>).
/// </summary>
internal readonly record struct DbValueSpec<T>(string ParameterName, DbKind Kind, int? Size = null);
