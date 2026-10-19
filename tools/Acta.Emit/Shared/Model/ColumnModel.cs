using System.Reflection;

namespace Acta.Emit.Shared.Model;

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
    public bool CaseExact => _spec.CaseExact;
    public bool IsNullable => _spec.IsNullable;
    public DbDefault Default => _spec.Default;
    public bool IsPrimaryKey => _spec.IsPrimaryKey;
    public bool IsIdentity => _spec.IsIdentity;
    public bool IsConcurrencyToken => _spec.IsConcurrencyToken;
    public bool IsCoded => _spec.IsCoded;
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
