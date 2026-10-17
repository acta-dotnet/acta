namespace Acta;

/// <summary>
/// One lane in one namespace: the Jobs that reference it run one at a time, in job-id order. Enqueue
/// upserts the row by <c>(namespace_id, name)</c> and holds its row lock until the enqueuing
/// transaction commits, so laned job ids are allocated in commit order. Every settle of a laned Job
/// takes the same lock before it promotes the next Blocked member, and retention takes it before it
/// deletes a lane no runtime references.
/// </summary>
[DbTable("lanes")]
[DbPrimaryKey(Name = "pk_lanes", Columns = ["id"])]
[DbUniqueIndex(Name = "ux_lanes_namespace_name", Columns = ["namespace_id", "name"], Usage = "uniqueness")]
internal sealed class LaneEntity : IEntity<long>
{
    /// <summary>
    /// DB-assigned identity, referenced by <c>runtimes.lane_id</c>.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Owning namespace; logical FK to <c>namespaces.id</c> (no enforced FK, SP-side validation).
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Caller-supplied lane, a name: trimmed, lowercased, dotted kebab of 1 to 128 characters. The C# layer
    /// canonicalizes it before any write.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>When the lane was first used. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }
}
