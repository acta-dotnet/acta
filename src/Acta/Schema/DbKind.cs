namespace Acta;

/// <summary>
/// Physical storage-kind vocabulary used by <see cref="DbColumnAttribute"/>; provider DDL is derived
/// per-provider from this value (docs/architecture-decisions.md, "Storage and naming"). Describes
/// physical storage only; a code column is inferred from the CLR property being a <c>[CodeKind]</c>
/// enum, not encoded here, and stores <see cref="Byte"/>.
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

    // ---------- Bounded strings (Size required; -1 = provider max) ----------

    /// <summary>ASCII-only varchar; validated at the application boundary.</summary>
    AsciiString,

    /// <summary>Unicode-capable varchar/nvarchar; avoid on hot tables.</summary>
    UnicodeString,

    // ---------- Unbounded / large values ----------

    /// <summary>
    /// Unbounded opaque bytes: MSSQL <c>varbinary(max)</c>, PG <c>bytea</c>, SQLite <c>BLOB</c>.
    /// SPs treat the value as opaque (no <c>ISJSON</c>); validation lives at the application
    /// boundary.
    /// </summary>
    BinaryPayload,
}
