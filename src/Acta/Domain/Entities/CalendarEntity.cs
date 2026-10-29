namespace Acta;

/// <summary>
/// One immutable content version of a namespace's named catalog calendar. The rows sharing
/// <c>(namespace_id, calendar_name)</c> are the calendar's family, numbered from 1 with no gap; the family's first row
/// fixes its role, and its one Published row is its current version. An operator saves drafts, publishes, and archives
/// under the namespace's exclusive schedule lock; a version's definition never changes, only its status.
/// </summary>
[DbTable("calendars")]
[DbPrimaryKey(Name = "pk_calendars", Columns = ["calendar_id"])]
[DbUniqueIndex(Name = "ux_calendars_namespace_name_version", Columns = ["namespace_id", "calendar_name", "version"], Usage = "uniqueness")]
[DbUniqueIndex(
    Name = "ux_calendars_current",
    Columns = ["namespace_id", "calendar_name"],
    Filter = "calendar_status_code = 20",
    Usage = "uniqueness"
)]
[DbForeignKey(
    Name = "fk_calendars_namespaces",
    Target = typeof(NamespaceEntity),
    TargetColumn = "namespace_id",
    Column = "namespace_id",
    OnDelete = DbForeignKeyAction.NoAction
)]
[DbCheck(Name = "ck_calendars_version", Sql = "version > 0")]
internal sealed class CalendarEntity : IEntity<int>
{
    /// <summary>
    /// Primary key, database-assigned at INSERT and never reused: the subject of the version's calendar.* events.
    /// Internal: the public selector is the namespace, the calendar name, and the version.
    /// </summary>
    [DbColumn("calendar_id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>The namespace whose catalog holds the calendar.</summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>The family's name, dotted kebab-case, unique within the namespace; a schedule binds the family by it.</summary>
    [DbColumn("calendar_name", DbKind.AsciiString, Size = 128)]
    public string CalendarName { get; init; } = default!;

    /// <summary>The version within the family, from 1; a draft takes the family's latest version plus one.</summary>
    [DbColumn("version", DbKind.Int32)]
    public int Version { get; init; }

    /// <summary>Whether the calendar supplies occurrences or availability windows; the same on every row of a family.</summary>
    [DbColumn("calendar_role_code")]
    public CalendarRoleCode Role { get; init; }

    /// <summary>The payload format of <see cref="Definition"/>: JSON (1).</summary>
    [DbColumn("definition_format_id", DbKind.Byte, Default = DbDefault.Zero)]
    public byte DefinitionFormatId { get; init; }

    /// <summary>The declarative definition in its canonical form, UTF-8 JSON without insignificant whitespace.</summary>
    [DbColumn("definition", DbKind.BinaryPayload)]
    public byte[] Definition { get; init; } = default!;

    /// <summary>SHA-256 hex of <see cref="Definition"/>, so two versions with the same content compare equal.</summary>
    [DbColumn("content_hash", DbKind.AsciiString, Size = 64)]
    public string ContentHash { get; init; } = default!;

    /// <summary>Draft, Published (the family's current version), Superseded, or Archived.</summary>
    [DbColumn("calendar_status_code")]
    public CalendarStatusCode Status { get; init; }

    /// <summary>When the version was saved. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>When the version was published; NULL for a version that never was.</summary>
    [DbColumn("published_at_utc", DbKind.UtcInstant)]
    public DateTime? PublishedAtUtc { get; init; }

    /// <summary>When the row was last written: saved, published, superseded, or archived.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; init; }
}
