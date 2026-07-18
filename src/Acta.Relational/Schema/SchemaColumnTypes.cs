namespace Acta.Relational.Schema;

/// <summary>
/// Physical storage-kind vocabulary used by <see cref="DbColumnAttribute"/>; provider DDL is derived
/// per-provider from this value (mapping table in docs/internals/design.md, "Persistence and naming"). Describes
/// physical storage only; "coded" (enum-backed) columns are inferred from the CLR property being an
/// enum, not encoded here; such a column stores its enum's underlying width (<see cref="Byte"/> /
/// <see cref="Int16"/> / <see cref="Int32"/>) plus a generated CHECK over the enum's known values.
/// </summary>
internal enum DbKind
{
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Guid,

    /// <summary>
    /// UTC instant; MSSQL <c>datetime2(3)</c>, PG <c>timestamptz</c>, SQLite <c>TEXT</c> (ISO 8601).
    /// </summary>
    UtcInstant,
    Decimal,

    // ---------- Bounded strings (Size required; -1 = provider max) ----------

    /// <summary>ASCII-only varchar; validated at the application boundary.</summary>
    AsciiString,

    /// <summary>Unicode-capable varchar/nvarchar; avoid on hot tables.</summary>
    UnicodeString,

    // ---------- Bounded binary (Size required) ----------

    /// <summary>
    /// Bounded binary: MSSQL <c>varbinary(N)</c>, PG <c>bytea</c> with <c>CHECK (octet_length(col) &lt;= N)</c>,
    /// SQLite <c>BLOB</c>. <c>Size</c> is required and is the maximum byte length; for unbounded
    /// opaque bytes use <see cref="BinaryPayload"/>.
    /// </summary>
    Bytes,

    // ---------- Unbounded / large values ----------

    /// <summary>
    /// Unbounded opaque bytes: MSSQL <c>varbinary(max)</c>, PG <c>bytea</c>, SQLite <c>BLOB</c>.
    /// SPs treat the value as opaque (no <c>ISJSON</c>); validation lives at the application
    /// boundary. For bounded byte sequences use <see cref="Bytes"/>.
    /// </summary>
    BinaryPayload,
}

/// <summary>
/// Provider-portable default-value selector for <see cref="DbColumnAttribute.Default"/>. The framework
/// renders the provider-native DEFAULT clause from this value; consumer code never writes the literal SQL.
/// </summary>
/// <remarks>
/// Per-value rendering (MSSQL / PostgreSQL): <see cref="None"/> emits no DEFAULT (caller supplies the
/// value at INSERT); <see cref="UtcNow"/> renders <c>SYSUTCDATETIME()</c> / <c>now()</c>;
/// <see cref="Zero"/> renders <c>0</c>; <see cref="EmptyString"/> renders <c>''</c>;
/// <see cref="NewGuid"/> renders <c>NEWID()</c> / <c>gen_random_uuid()</c>.
/// Each value is compatible only with matching <see cref="DbKind"/>s (for example <see cref="UtcNow"/>
/// requires <see cref="DbKind.UtcInstant"/>), and the generator emits an <c>ACTA0403</c> diagnostic on a
/// mismatch. Using an enum instead of a free-form SQL string keeps defaults provider-portable; the
/// generator owns the grammar. Operations INSERTing a row whose columns carry a non-<see cref="None"/>
/// default must omit those columns from the INSERT column list and let the DEFAULT fire.
/// </remarks>
internal enum DbDefault
{
    /// <summary>No DEFAULT emitted. The caller must supply the value at INSERT.</summary>
    None = 0,

    /// <summary>DB UTC at row creation. Compatible only with <see cref="DbKind.UtcInstant"/>.</summary>
    UtcNow = 1,

    /// <summary>Numeric zero. Compatible with the integer and decimal kinds.</summary>
    Zero = 2,

    /// <summary>Empty string. Compatible with <see cref="DbKind.AsciiString"/> and <see cref="DbKind.UnicodeString"/>.</summary>
    EmptyString = 3,

    /// <summary>Server-generated UUID. Compatible only with <see cref="DbKind.Guid"/>.</summary>
    NewGuid = 4,
}

/// <summary>
/// Generator-emitted scalar parameter descriptor for SQL-only parameters (not bound to a column).
/// <see cref="ActaSchema.Sql"/> exposes these by name; provider code calls <c>DbParams.For</c> to materialize
/// the bound <c>DbParameterSpec</c>. Enum-backed parameters carry their physical integer
/// <see cref="DbKind"/> (<see cref="DbKind.Byte"/> / <see cref="DbKind.Int16"/> / <see cref="DbKind.Int32"/>).
/// </summary>
internal readonly record struct DbValueSpec<T>(string ParameterName, DbKind Kind, int? Size, int? Precision, int? Scale, bool IsNullable);
