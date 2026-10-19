namespace Acta;

/// <summary>
/// One column on a persisted entity, every fact declared via <see cref="DbColumnAttribute"/> and the entity-level
/// attributes, consumed by provider operations (schema-aware SQL) and the schema emitters (migration DDL). A coded
/// column (<see cref="IsCoded"/>) is a <c>[CodeKind]</c> enum property stored as <see cref="DbKind.Byte"/>, with
/// <see cref="EnumTypeName"/> and the <see cref="CodeKind"/> discriminator for catalog seeding and docs; the provider
/// allocates an identity column at INSERT (a sole integer key not marked manual, or a composite key's declared identity);
/// a concurrency token increments via <c>SET version = version + 1</c>; <see cref="Generated"/> is a stored computed column's SQL.
/// </summary>
internal sealed record DbColumnSpec(
    string Name,
    DbKind Kind,
    int? Size = null,
    bool IsNullable = false,
    DbDefault Default = DbDefault.None,
    bool IsCoded = false,
    bool IsPrimaryKey = false,
    bool IsIdentity = false,
    bool IsConcurrencyToken = false,
    string? EnumTypeName = null,
    string? CodeKind = null,
    string ClrPropertyName = "",
    string? Generated = null,
    bool CaseExact = false
)
{
    /// <summary>
    /// <c>true</c> iff this column is store-generated (computed) from <see cref="Generated"/>. Such
    /// columns are STORED/PERSISTED and read-only: never named in an INSERT/UPDATE column list.
    /// </summary>
    public bool IsGenerated => Generated is not null;

    /// <summary>
    /// <c>true</c> iff a server-side DEFAULT will fire on INSERT. Provider operations that supply this
    /// column's value would conflict with the DEFAULT, so they must omit it from the INSERT column list
    /// and let the server populate the value.
    /// </summary>
    public bool HasServerDefault => Default != DbDefault.None;
}

/// <summary>
/// Typed handle for a column; adds the parameter name the runtime uses when binding
/// <typeparamref name="T"/> values to the untyped <see cref="DbColumnSpec"/>.
/// </summary>
internal readonly struct DbColumnSpec<T>(DbColumnSpec untyped, string parameterName)
{
    public DbColumnSpec Untyped { get; } = untyped;

    public string ParameterName { get; } = parameterName;

    public DbKind Kind => Untyped.Kind;

    public int? Size => Untyped.Size;

    public static implicit operator DbColumnSpec(DbColumnSpec<T> spec) => spec.Untyped;
}
