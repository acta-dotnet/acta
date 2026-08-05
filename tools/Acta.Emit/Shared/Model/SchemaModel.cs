using System.Reflection;
using Acta.Relational.Entities;
using Acta.Relational.Schema;

namespace Acta.Emit.Shared.Model;

/// <summary>
/// Emit-time projection of the runtime entity manifest, decorated with XML-doc summaries.
/// </summary>
internal sealed class SchemaModel
{
    public required IReadOnlyList<EntityModel> Entities { get; init; }

    public static SchemaModel Discover()
    {
        var docs = XmlDocSource.ForAssembly(typeof(Job).Assembly);

        var entities = ActaSchema
            .Entities.Select(spec => new EntityModel(spec, docs))
            .OrderBy(e => e.TableName, StringComparer.Ordinal)
            .ToList();

        return new SchemaModel { Entities = entities };
    }
}

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
    public IReadOnlyList<ColumnModel> Columns { get; } = spec.Columns.Select(c => new ColumnModel(c, spec.ClrType, docs)).ToList();
    public IReadOnlyList<DbIndexSpec> Indexes => _spec.Indexes;
    public IReadOnlyList<DbCheckSpec> Checks => _spec.Checks;
    public IReadOnlyList<DbForeignKeySpec> ForeignKeys => _spec.ForeignKeys;
    public DbPrimaryKeySpec PrimaryKey => _spec.PrimaryKey;

    public string? Summary => _docs.ForType(_spec.ClrType);
}

/// <summary>
/// Emit-time view of one column (structural metadata + XML-doc summary + lazy
/// <see cref="PropertyInfo"/>).
/// </summary>
internal sealed class ColumnModel(DbColumnSpec spec, Type entityType, XmlDocSource docs)
{
    private readonly DbColumnSpec _spec = spec;
    private readonly Type _entityType = entityType;
    private readonly XmlDocSource _docs = docs;

    public string Name => _spec.Name;
    public DbKind Kind => _spec.Kind;
    public int Size => _spec.Size ?? 0;
    public int Precision => _spec.Precision ?? 0;
    public int Scale => _spec.Scale ?? 0;
    public bool IsNullable => _spec.IsNullable;
    public DbDefault Default => _spec.Default;
    public bool IsPrimaryKey => _spec.IsPrimaryKey;
    public bool IsSolePrimaryKey => _spec.IsSolePrimaryKey;
    public bool IsManualPrimaryKey => _spec.IsManualPrimaryKey;
    public bool IsConcurrencyToken => _spec.IsConcurrencyToken;
    public bool IsCoded => _spec.IsCoded;
    public bool IsExtensible => _spec.IsExtensible;
    public string? EnumTypeName => _spec.EnumTypeName;
    public string? CodeKind => _spec.CodeKind;
    public string? Generated => _spec.Generated;
    public bool IsGenerated => _spec.IsGenerated;

    public PropertyInfo Property =>
        field ??=
            _entityType.GetProperty(_spec.ClrPropertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{_spec.ClrPropertyName}' not found on {_entityType.Name}.");

    public string? Summary => _docs.ForProperty(Property);
}
