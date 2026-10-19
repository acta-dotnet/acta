namespace Acta;

/// <summary>
/// Class-level multi-column CHECK constraint. `Sql` references DB column names; validated lexically.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbCheckAttribute : Attribute
{
    /// <summary>
    /// Constraint name; must carry the `ck_` prefix.
    /// </summary>
    public required string Name { get; init; }
    public required string Sql { get; init; }
}
