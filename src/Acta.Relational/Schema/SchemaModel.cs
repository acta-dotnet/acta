namespace Acta.Relational.Schema;

/// <summary>
/// One entity's complete persistence shape: the runtime mirror of everything the attribute layer
/// declares on a <c>[DbTable]</c> class.
/// </summary>
/// <remarks>
/// Instances are emitted as literals by <c>ActaDbGenerator</c> into <c>ActaSchema.Generated.cs</c>,
/// with no runtime reflection. Reach an instance via <c>ActaSchema.Job.Entity</c> (typed) or
/// <c>ActaSchema.For&lt;TEntity&gt;()</c> (generic).
/// </remarks>
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
/// One column on a persisted entity, capturing every fact declared via
/// <see cref="DbColumnAttribute"/> and the surrounding entity-level attributes. Consumed by
/// provider operations (schema-aware SQL rendering) and schema emitters (migration DDL).
/// </summary>
/// <param name="Name">Snake_case column name.</param> <param name="Kind">Storage kind (drives provider type mapping).</param> <param name="Size">For Ascii/Unicode/Bytes: max byte/char length; <c>null</c> elsewhere; <c>-1</c> means max length.</param>
/// <param name="Precision">Decimal total digits.</param> <param name="Scale">Decimal digits right of the point.</param> <param name="IsNullable">Whether the CLR property is nullable (<c>T?</c> or reference + nullable annotation).</param>
/// <param name="Default">Server-side DEFAULT selector (<see cref="DbDefault.None"/> = no DEFAULT, caller supplies the value at INSERT).</param>
/// <param name="IsCoded"><c>true</c> iff the CLR property is an enum (the generator sets this). Coded columns store their enum's underlying width in <see cref="Kind"/> (<see cref="DbKind.Byte"/> / <see cref="DbKind.Int16"/> / <see cref="DbKind.Int32"/>) and get a generated CHECK over the enum's known values.</param>
/// <param name="IsPrimaryKey"><c>true</c> iff this column is part of the entity's PK.</param> <param name="IsSolePrimaryKey"><c>true</c> iff the PK has exactly one column and this is it (drives identity emission).</param>
/// <param name="IsManualPrimaryKey"><c>true</c> iff the PK is single-column with <c>Manual = true</c> (suppresses provider-native identity; caller assigns at INSERT).</param>
/// <param name="IsConcurrencyToken"><c>true</c> iff the column carries <c>DbConcurrencyToken</c>; UPDATEs increment via <c>SET version = version + 1</c>.</param>
/// <param name="EnumTypeName">CLR enum type name when the property is an enum; <c>null</c> otherwise; drives catalog seeding and docs.</param>
/// <param name="CodeKind">Kebab catalog discriminator the source generator baked into the enum's companion (e.g. <c>"job-status"</c>); <c>null</c> when the enum has no companion (not a <c>[Code]</c> family) or the column is not enum-backed.</param>
/// <param name="ClrPropertyName">The C# property name on the entity; used by emit-time tooling that reflects on the property (XML-doc lookup, diagnostics).</param>
/// <param name="Generated">SQL expression for a store-generated (STORED/PERSISTED) computed column; <c>null</c> for an ordinary column.</param>
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
    string? Generated = null
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
internal readonly struct DbColumnSpec<T>
{
    public DbColumnSpec(DbColumnSpec untyped, string table, string column, string parameterName)
    {
        Untyped = untyped;
        Table = table;
        Column = column;
        ParameterName = parameterName;
    }

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

    public DbColumnSpec Untyped { get; }

    public string Table { get; }

    public string Column { get; }

    public string ParameterName { get; }

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
/// One enforced foreign-key declaration on an entity. Acta declares enforced FKs only where the
/// child row's lifetime is bounded by the parent's (see <c>docs/internals/design.md</c>, FK policy).
/// </summary>
/// <param name="Name">Constraint name; must carry the <c>fk_</c> prefix.</param>
/// <param name="Column">Snake_case local column name on the declaring entity.</param>
/// <param name="Target">The referenced entity type.</param>
/// <param name="TargetColumn">Snake_case column name on the target entity.</param>
/// <param name="OnDelete">Provider <c>ON DELETE</c> action.</param>
internal sealed record DbForeignKeySpec(string Name, string Column, Type Target, string TargetColumn, DbForeignKeyAction OnDelete);

/// <summary>
/// One index declared on an entity; unique and non-unique declarations collapse onto this single
/// shape with <see cref="IsUnique"/> as the discriminator.
/// </summary>
/// <param name="Name">Constraint name; <c>ix_*</c> for non-unique, <c>ux_*</c> for unique.</param>
/// <param name="Columns">Index key columns in declaration order.</param>
/// <param name="Includes">Optional INCLUDE columns (covering-index payload). <c>null</c> when none.</param>
/// <param name="Descending">Optional list of key columns sorted descending. <c>null</c> when none.</param>
/// <param name="Filter">Optional filter predicate (filtered indexes / partial indexes). <c>null</c> when none.</param>
/// <param name="Usage">The access pattern the index serves (for example <c>claim_hot_path</c>). Documentation-only.</param>
/// <param name="IsUnique"><c>true</c> for unique indexes.</param>
internal sealed record DbIndexSpec(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string>? Includes,
    IReadOnlyList<string>? Descending,
    string? Filter,
    string Usage,
    bool IsUnique
);

/// <summary>
/// One multi-column CHECK constraint declared on an entity. <see cref="Sql"/> references column
/// names lexically; validation is the emitter's job, not the runtime's.
/// </summary>
/// <param name="Name">Constraint name; must carry the <c>ck_</c> prefix.</param>
/// <param name="Sql">Provider-agnostic boolean expression (e.g.,
/// <c>"input_format_id = 0 AND input IS NULL OR input_format_id &lt;&gt; 0 AND input IS NOT NULL"</c>).</param>
internal sealed record DbCheckSpec(string Name, string Sql);
