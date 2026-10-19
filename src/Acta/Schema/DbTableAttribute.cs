namespace Acta;

// Schema attribute family. All names declared on these attributes are explicit
// lower_snake_case and schema-relative (the schema component is the install-time provider option).
// See docs/design.md, "Storage and naming", for the full ledger.

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

    /// <summary>
    /// Opts this table out of SQL Server lock escalation, for a table one statement writes a thousand rows of while
    /// other sessions read it under locks; no-op on providers that do not escalate.
    /// </summary>
    public bool DisableLockEscalation { get; init; }
}
