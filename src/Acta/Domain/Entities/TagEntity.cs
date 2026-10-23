namespace Acta;

/// <summary>
/// Exact searchable metadata attached to one entity. A tag belongs to exactly the entity it names: it does not
/// inherit, fall back, propagate, or participate in precedence resolution.
/// </summary>
[DbTable("tags")]
[DbPrimaryKey(Name = "pk_tags", Columns = ["entity_code", "entity_id", "tag_name"])]
[DbCheck(Name = "ck_tags_entity_id", Sql = "entity_id > 0")]
[DbCheck(
    Name = "ck_tags_value_search",
    Sql = "(tag_value IS NULL AND tag_value_search IS NULL) OR (tag_value IS NOT NULL AND tag_value_search IS NOT NULL)"
)]
internal sealed class TagEntity : IEntity
{
    /// <summary>Kind of the tagged entity.</summary>
    [DbColumn("entity_code")]
    public EntityCode EntityCode { get; init; }

    /// <summary>Tagged row id, with smaller entity identifiers widened to bigint.</summary>
    [DbColumn("entity_id", DbKind.Int64)]
    public long EntityId { get; init; }

    /// <summary>Normalized dotted-kebab ASCII name.</summary>
    [DbColumn("tag_name", DbKind.AsciiString, Size = 128)]
    public string TagName { get; init; } = default!;

    /// <summary>Optional caller-preserved Unicode value; null represents a presence-only tag.</summary>
    [DbColumn("tag_value", DbKind.UnicodeString, Size = 128)]
    public string? TagValue { get; init; }

    /// <summary>Length-checked exact-search projection generated in .NET: the value trimmed and upper-cased.</summary>
    [DbColumn("tag_value_search", DbKind.UnicodeString, Size = 128, CaseExact = true)]
    public string? TagValueSearch { get; init; }
}
