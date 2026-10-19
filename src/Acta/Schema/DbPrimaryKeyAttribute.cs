namespace Acta;

/// <summary>
/// Class-level primary-key declaration (single-column or composite). Eligible single-column
/// integer PKs auto-emit provider-native identity allocation; set <see cref="Manual"/> to suppress,
/// or name the allocated column of a composite key in <see cref="Identity"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class DbPrimaryKeyAttribute : Attribute
{
    /// <summary>
    /// Constraint name; must carry the `pk_` prefix.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Snake_case DB column names; composite PKs list each column in key order.
    /// </summary>
    public required string[] Columns { get; init; }

    /// <summary>
    /// Suppress provider-native identity emission on an otherwise-eligible single-column integer PK;
    /// the caller supplies the id at INSERT.
    /// </summary>
    public bool Manual { get; init; }

    /// <summary>
    /// The integer column of a composite key whose value the provider allocates. SQLite makes it the
    /// <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> rowid alias and so the table's whole key, because
    /// AUTOINCREMENT requires the rowid alias to be the primary key.
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// Opts the clustered PK into SQL Server <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; reserve for
    /// high-insert tables with monotonically increasing keys. No-op on providers without the hint.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}
