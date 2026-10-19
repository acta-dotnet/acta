namespace Acta;

/// <summary>
/// Required on every persisted property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class DbColumnAttribute : Attribute
{
    /// <summary>
    /// Code column: the property is a byte-backed <c>[CodeKind]</c> enum, stored as <see cref="DbKind.Byte"/>.
    /// </summary>
    public DbColumnAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Non-enum column: declares the physical storage <see cref="DbKind"/> explicitly. Use on non-enum properties.
    /// </summary>
    public DbColumnAttribute(string name, DbKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public DbKind Kind { get; }

    /// <summary>
    /// Required for <see cref="DbKind.AsciiString"/> and <see cref="DbKind.UnicodeString"/>, where -1 is max; ignored elsewhere.
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// The column holds a key or a search projection compared bytewise, so a provider whose default collation folds case gives it a
    /// binary one (SQL Server <c>Latin1_General_100_BIN2_UTF8</c>); PostgreSQL and SQLite already compare exactly.
    /// </summary>
    public bool CaseExact { get; init; }

    /// <summary>
    /// Server-side default. <see cref="DbDefault.None"/> emits no DEFAULT clause (caller supplies the
    /// value at INSERT); any other value renders the provider-native DEFAULT clause (mapping table on
    /// <see cref="DbDefault"/>). The generator emits <c>ACTA0403</c> on
    /// <see cref="DbKind"/> / <see cref="DbDefault"/> mismatches.
    /// </summary>
    public DbDefault Default { get; init; } = DbDefault.None;

    /// <summary>
    /// SQL expression for a store-generated (computed) column; <c>null</c> = an ordinary column.
    /// References sibling snake_case column names and must be ANSI-portable (the same literal renders on
    /// every provider). Generated columns are always STORED/PERSISTED and read-only: never bound on
    /// INSERT/UPDATE, materialized by the DB, read back on SELECT. Mutually exclusive with
    /// <see cref="Default"/>.
    /// </summary>
    public string? Generated { get; init; }
}
