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
