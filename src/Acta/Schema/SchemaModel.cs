namespace Acta;

/// <summary>
/// One entity's complete persistence shape: the runtime mirror of everything the attribute layer declares on a
/// <c>[DbTable]</c> class. Instances are emitted as literals by the schema generator, with no runtime reflection;
/// reach one via <c>ActaSchema.Job.Entity</c> (typed) or <c>ActaSchema.For&lt;TEntity&gt;()</c> (generic).
/// </summary>
internal sealed class DbEntitySpec
{
    /// <summary>The CLR entity type.</summary>
    public required System.Type ClrType { get; init; }

    /// <summary>Snake_case table name (no schema prefix).</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="DbTableAttribute.PageCompression"/> opts this table into SQL
    /// Server <c>DATA_COMPRESSION = PAGE</c>; no-op on providers without table compression.
    /// </summary>
    public bool PageCompression { get; init; }

    /// <summary>
    /// <c>true</c> when <see cref="DbTableAttribute.DisableLockEscalation"/> opts this table out of SQL Server lock
    /// escalation; no-op on providers that do not escalate.
    /// </summary>
    public bool DisableLockEscalation { get; init; }

    /// <summary>All columns in declaration order.</summary>
    public required IReadOnlyList<DbColumnSpec> Columns { get; init; }

    /// <summary>Primary-key declaration (composite-capable).</summary>
    public required DbPrimaryKeySpec PrimaryKey { get; init; }

    /// <summary>All indexes (unique and non-unique) declared on the entity.</summary>
    public required IReadOnlyList<DbIndexSpec> Indexes { get; init; }

    /// <summary>All multi-column CHECK constraints declared on the entity.</summary>
    public required IReadOnlyList<DbCheckSpec> Checks { get; init; }

    /// <summary>All enforced foreign keys declared on the entity.</summary>
    public required IReadOnlyList<DbForeignKeySpec> ForeignKeys { get; init; }

    /// <summary>
    /// Looks up a column by snake_case name; throws if unknown. Linear scan, used only on cold
    /// paths (test inspection, drift checks).
    /// </summary>
    public DbColumnSpec Column(string name)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Name == name)
            {
                return Columns[i];
            }
        }
        var available = string.Join(", ", System.Linq.Enumerable.Select(Columns, c => c.Name));
        throw new System.InvalidOperationException($"Entity '{TableName}' has no [DbColumn] named '{name}'. Available: {available}");
    }
}

/// <summary>
/// One column on a persisted entity, every fact declared via <see cref="DbColumnAttribute"/> and the entity-level
/// attributes, consumed by provider operations (schema-aware SQL) and the schema emitters (migration DDL). A coded
/// column (<see cref="IsCoded"/>) is an enum property storing its underlying width in <see cref="Kind"/>, with
/// <see cref="EnumTypeName"/> and the <see cref="CodeKind"/> discriminator for catalog seeding and docs; a manual
/// primary key suppresses provider-native identity, so the caller assigns it at INSERT; a concurrency token
/// increments on UPDATE via <c>SET version = version + 1</c>; <see cref="Generated"/> is a stored computed column's SQL.
/// </summary>
internal sealed record DbColumnSpec(
    string Name,
    DbKind Kind,
    int? Size = null,
    int? Precision = null,
    int? Scale = null,
    bool IsNullable = false,
    DbDefault Default = DbDefault.None,
    bool IsCoded = false,
    bool IsPrimaryKey = false,
    bool IsSolePrimaryKey = false,
    bool IsManualPrimaryKey = false,
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
internal readonly struct DbColumnSpec<T>(DbColumnSpec untyped, string table, string column, string parameterName)
{
    public DbColumnSpec(
        string table,
        string column,
        string parameterName,
        DbKind kind,
        int? size,
        int? precision,
        int? scale,
        bool isNullable,
        bool isCoded = false
    )
        : this(
            new DbColumnSpec(
                Name: column,
                Kind: kind,
                Size: size,
                Precision: precision,
                Scale: scale,
                IsNullable: isNullable,
                IsCoded: isCoded
            ),
            table,
            column,
            parameterName
        ) { }

    public DbColumnSpec Untyped { get; } = untyped;

    public string Table { get; } = table;

    public string Column { get; } = column;

    public string ParameterName { get; } = parameterName;

    public string Name => Untyped.Name;

    public DbKind Kind => Untyped.Kind;

    public int? Size => Untyped.Size;

    public int? Precision => Untyped.Precision;

    public int? Scale => Untyped.Scale;

    public bool IsNullable => Untyped.IsNullable;

    public DbDefault Default => Untyped.Default;

    public bool IsCoded => Untyped.IsCoded;

    public bool IsPrimaryKey => Untyped.IsPrimaryKey;

    public bool IsSolePrimaryKey => Untyped.IsSolePrimaryKey;

    public bool IsManualPrimaryKey => Untyped.IsManualPrimaryKey;

    public bool IsConcurrencyToken => Untyped.IsConcurrencyToken;

    public string? EnumTypeName => Untyped.EnumTypeName;

    public string? CodeKind => Untyped.CodeKind;

    public string ClrPropertyName => Untyped.ClrPropertyName;

    public bool HasServerDefault => Untyped.HasServerDefault;

    public static implicit operator DbColumnSpec(DbColumnSpec<T> spec) => spec.Untyped;
}

/// <summary>
/// The primary-key declaration on an entity. <see cref="Columns"/> lists every column in
/// declaration order; <see cref="Manual"/> suppresses provider-native identity emission on an
/// otherwise-eligible single-column integer PK.
/// </summary>
internal sealed record DbPrimaryKeySpec(string Name, IReadOnlyList<string> Columns, bool Manual = false)
{
    /// <summary>
    /// <c>true</c> when <see cref="DbPrimaryKeyAttribute.OptimizeForSequentialKey"/> opts the
    /// clustered PK into SQL Server <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; no-op elsewhere.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}

/// <summary>
/// One enforced foreign-key declaration on an entity, <paramref name="Column"/> to <paramref name="TargetColumn"/>
/// on <paramref name="Target"/> with the provider's <paramref name="OnDelete"/> action; the name carries the
/// <c>fk_</c> prefix. Acta declares enforced FKs only where the child row's lifetime is bounded by the parent's
/// (<c>docs/internals/design.md</c>, FK policy).
/// </summary>
internal sealed record DbForeignKeySpec(string Name, string Column, Type Target, string TargetColumn, DbForeignKeyAction OnDelete);

/// <summary>
/// One index declared on an entity; unique (<c>ux_*</c>) and non-unique (<c>ix_*</c>) declarations collapse onto this
/// shape with <see cref="IsUnique"/> as the discriminator. <see cref="Columns"/> are the key columns in declaration
/// order; <see cref="Includes"/>, <see cref="Descending"/>, and <see cref="Filter"/> are null when absent;
/// <see cref="Usage"/> names the access pattern the index serves, for documentation only.
/// </summary>
internal sealed record DbIndexSpec(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string>? Includes,
    IReadOnlyList<string>? Descending,
    string? Filter,
    string Usage,
    bool IsUnique
)
{
    /// <summary>
    /// <c>true</c> when <see cref="DbIndexAttribute.OptimizeForSequentialKey"/> opts this index into
    /// SQL Server <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; no-op elsewhere.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}

/// <summary>
/// One multi-column CHECK constraint declared on an entity. <see cref="Sql"/> references column
/// names lexically; validation is the emitter's job, not the runtime's.
/// </summary>
/// <param name="Name">Constraint name; must carry the <c>ck_</c> prefix.</param>
/// <param name="Sql">Provider-agnostic boolean expression (e.g.,
/// <c>"input_format_id = 0 AND input IS NULL OR input_format_id &lt;&gt; 0 AND input IS NOT NULL"</c>).</param>
internal sealed record DbCheckSpec(string Name, string Sql);
