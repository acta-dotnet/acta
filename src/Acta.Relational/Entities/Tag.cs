using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Exact searchable metadata attached to one target. A tag scope identifies the exact target to which
/// searchable metadata is attached. Tag scopes do not inherit, fall back, propagate, or participate in
/// precedence resolution.
/// </summary>
[DbTable("tags")]
[DbPrimaryKey(Name = "pk_tags", Columns = ["scope_code", "scope_id", "name"])]
[DbIndex(
    Name = "ix_tags_namespace_name_value_search",
    Columns = ["namespace_id", "name", "value_search", "scope_code", "scope_id"],
    Usage = "dashboard"
)]
[DbCheck(Name = "ck_tags_scope_id", Sql = "scope_id > 0")]
[DbCheck(Name = "ck_tags_namespace_id", Sql = "namespace_id IS NULL OR namespace_id > 0")]
[DbCheck(
    Name = "ck_tags_scope_namespace",
    Sql = "(scope_code = 20 AND namespace_id IS NULL) OR "
        + "(scope_code = 30 AND namespace_id IS NOT NULL AND scope_id = namespace_id) OR "
        + "(scope_code IN (40, 50, 60, 70, 80, 90) AND namespace_id IS NOT NULL)"
)]
[DbCheck(
    Name = "ck_tags_value_search",
    Sql = "(value IS NULL AND value_search IS NULL) OR (value IS NOT NULL AND value_search IS NOT NULL)"
)]
internal sealed class Tag : IEntity
{
    /// <summary>Exact target type; independent of <c>SettingScopeCode</c>.</summary>
    [DbColumn("scope_code")]
    public TagScopeCode ScopeCode { get; init; }

    /// <summary>Target row id, with smaller target identifiers widened to bigint.</summary>
    [DbColumn("scope_id", DbKind.Int64)]
    public long ScopeId { get; init; }

    /// <summary>
    /// Owning namespace for reverse search. Tenant tags are null; namespace tags carry their own id;
    /// every other target carries its owning namespace.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int? NamespaceId { get; init; }

    /// <summary>Normalized dotted-kebab ASCII name.</summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>Optional caller-preserved Unicode value; null represents a presence-only tag.</summary>
    [DbColumn("value", DbKind.UnicodeString, Size = 128)]
    public string? Value { get; init; }

    /// <summary>Length-checked case-folded exact-search projection generated in .NET.</summary>
    [DbColumn("value_search", DbKind.UnicodeString, Size = 128)]
    public string? ValueSearch { get; init; }
}
