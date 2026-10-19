namespace Acta.Emit.Shared.Model;

/// <summary>
/// Emit-time view of one entity (structural metadata + XML-doc summary).
/// </summary>
internal sealed class EntityModel(DbEntitySpec spec, XmlDocSource docs)
{
    private readonly DbEntitySpec _spec = spec;
    private readonly XmlDocSource _docs = docs;

    public Type ClrType => _spec.ClrType;
    public string TableName => _spec.TableName;
    public bool PageCompression => _spec.PageCompression;
    public bool DisableLockEscalation => _spec.DisableLockEscalation;
    public IReadOnlyList<ColumnModel> Columns { get; } = spec.Columns.Select(c => new ColumnModel(c, spec.ClrType, docs)).ToList();
    public IReadOnlyList<DbIndexSpec> Indexes => _spec.Indexes;
    public IReadOnlyList<DbCheckSpec> Checks => _spec.Checks;
    public IReadOnlyList<DbForeignKeySpec> ForeignKeys => _spec.ForeignKeys;
    public DbPrimaryKeySpec PrimaryKey => _spec.PrimaryKey;

    public string? Summary => _docs.ForType(_spec.ClrType);
}
