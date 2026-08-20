namespace Acta.Relational.Schema;

// Schema attribute family. All names declared on these attributes are explicit
// lower_snake_case and schema-relative (the schema component is the install-time provider option).
// See docs/internals/design.md, "Persistence and naming", for the full ledger.

/// <summary>
/// Required on every <see cref="IEntity"/> implementation.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class DbTableAttribute(string name) : Attribute
{
    /// <summary>
    /// Snake_case table name (no schema prefix; schema is the install-time provider option).
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Opts this table into SQL Server <c>DATA_COMPRESSION = PAGE</c> (large/cold tables only);
    /// no-op on providers without table-level compression.
    /// </summary>
    public bool PageCompression { get; init; }
}

/// <summary>
/// Required on every persisted property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class DbColumnAttribute : Attribute
{
    /// <summary>
    /// Enum-backed (coded) column: the physical <see cref="DbKind"/> is inferred from the property's
    /// enum underlying type and a CHECK over the enum's known values is generated. Use only on enum properties.
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
        HasExplicitKind = true;
    }

    public string Name { get; }
    public DbKind Kind { get; }

    /// <summary>
    /// <c>true</c> iff a <see cref="DbKind"/> was supplied (non-enum column); <c>false</c> for the inferred
    /// enum form. The source generator decides explicit-vs-inferred via constructor-argument count; this
    /// flag mirrors that for non-generator readers.
    /// </summary>
    public bool HasExplicitKind { get; }

    /// <summary>
    /// Required for <see cref="DbKind.AsciiString"/>, <see cref="DbKind.UnicodeString"/>, and <see cref="DbKind.Bytes"/>;
    /// ignored elsewhere. For text kinds use -1 for max; for <see cref="DbKind.Bytes"/> the value is the
    /// maximum byte length and -1 / 0 are not permitted (use <see cref="DbKind.BinaryPayload"/> for unbounded).
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// For <see cref="DbKind.Decimal"/>: total digits.
    /// </summary>
    public int Precision { get; init; }

    /// <summary>
    /// For <see cref="DbKind.Decimal"/>: scale.
    /// </summary>
    public int Scale { get; init; }

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

/// <summary>
/// Class-level primary-key declaration (single-column or composite). Eligible single-column
/// integer PKs auto-emit provider-native identity allocation; set <see cref="Manual"/> to suppress.
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
    /// Opts the clustered PK into SQL Server <c>OPTIMIZE_FOR_SEQUENTIAL_KEY = ON</c>; reserve for
    /// high-insert tables with monotonically increasing keys. No-op on providers without the hint.
    /// </summary>
    public bool OptimizeForSequentialKey { get; init; }
}

/// <summary>
/// Optimistic-concurrency token. The property must be of type <c>int</c> and named
/// `Version` by convention; SPs manually increment via <c>SET version = version + 1</c> on every UPDATE.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class DbConcurrencyTokenAttribute : Attribute { }

/// <summary>
/// Exclude this property from persistence.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class DbIgnoreAttribute : Attribute { }

/// <summary>
/// Class-level non-unique index. All column lists use snake_case DB names.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbIndexAttribute : Attribute
{
    /// <summary>
    /// Index name; must carry the `ix_` prefix.
    /// </summary>
    public required string Name { get; init; }
    public required string[] Columns { get; init; }
    public string[]? Includes { get; init; }
    public string[]? Descending { get; init; }
    public string? Filter { get; init; }

    /// <summary>
    /// Why the index exists: the access pattern it serves, rendered into the data-model docs.
    /// Vocabulary: claim_hot_path, claim_horizon, read_api, dashboard, dashboard_grid, maintenance,
    /// uniqueness, lock_reclaim, scheduler, checkpoint_timer, heartbeat, child_fanout, alert_raise.
    /// Documentation-only; never affects DDL.
    /// </summary>
    public required string Usage { get; init; }
}

/// <summary>
/// Class-level unique index. Same shape as <see cref="DbIndexAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbUniqueIndexAttribute : Attribute
{
    /// <summary>
    /// Index name; must carry the `ux_` prefix.
    /// </summary>
    public required string Name { get; init; }
    public required string[] Columns { get; init; }
    public string[]? Includes { get; init; }
    public string[]? Descending { get; init; }
    public string? Filter { get; init; }

    /// <summary>
    /// Why the index exists: the access pattern it serves, rendered into the data-model docs.
    /// Vocabulary: claim_hot_path, claim_horizon, read_api, dashboard, dashboard_grid, maintenance,
    /// uniqueness, lock_reclaim, scheduler, checkpoint_timer, heartbeat, child_fanout, alert_raise.
    /// Documentation-only; never affects DDL.
    /// </summary>
    public required string Usage { get; init; }
}

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

/// <summary>
/// Class-level enforced foreign key. Acta declares enforced FKs only where the child's lifetime
/// is bounded by the parent's; audit/audit-adjacent tables carry none (see
/// <c>docs/internals/design.md</c>, FK policy).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class DbForeignKeyAttribute : Attribute
{
    /// <summary>
    /// Constraint name; must carry the `fk_` prefix.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The referenced entity type.
    /// </summary>
    public required Type Target { get; init; }

    /// <summary>
    /// Snake_case column name on the target entity (validated against the target's [DbColumn] declarations).
    /// </summary>
    public required string TargetColumn { get; init; }

    /// <summary>
    /// Snake_case local column name on the declaring entity.
    /// </summary>
    public required string Column { get; init; }

    /// <summary>
    /// Provider <c>ON DELETE</c> action; FKs that bound a child's lifetime by its parent's
    /// use <see cref="DbForeignKeyAction.Cascade"/>.
    /// </summary>
    public DbForeignKeyAction OnDelete { get; init; } = DbForeignKeyAction.NoAction;
}

/// <summary>
/// <c>ON DELETE</c> action emitted on a foreign key.
/// </summary>
internal enum DbForeignKeyAction
{
    /// <summary>Provider default; deletion of the parent row is rejected if children exist.</summary>
    NoAction,

    /// <summary>Deleting the parent row deletes the child rows in the same transaction.</summary>
    Cascade,

    /// <summary>Deleting the parent row NULLs the child's FK column (requires the column be nullable).</summary>
    SetNull,
}
