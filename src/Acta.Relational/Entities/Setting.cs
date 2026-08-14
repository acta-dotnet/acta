using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One durable configuration value in the central <c>settings</c> table, addressed by
/// <c>(scope_code, scope_id, name)</c>. Holds slow-changing operator/deployment configuration only,
/// never fast-changing runtime state (that lives on substrate tables).
/// Not read on any hot path yet; consumers resolve a setting by scope and fall back
/// (definition, then namespace, then global) at point of use.
/// Boundary rules: a value an operator filters by is a tag; a value the engine reads per-claim or
/// per-execution, or that must appear on the definition views, is a policy column (settings are
/// forbidden on the hot path); a behavior knob read cold by name at a scope is a setting.
/// </summary>
[DbTable("settings")]
[DbPrimaryKey(Name = "pk_settings", Columns = ["id"])]
[DbUniqueIndex(
    Name = "ux_settings_scope_name",
    Columns = ["scope_code", "scope_id", "name"],
    Filter = "scope_id IS NOT NULL",
    Usage = "uniqueness"
)]
[DbUniqueIndex(Name = "ux_settings_global_name", Columns = ["scope_code", "name"], Filter = "scope_id IS NULL", Usage = "uniqueness")]
[DbCheck(Name = "ck_settings_value_pair", Sql = "(value_format_id = 0 AND value IS NULL) OR (value_format_id <> 0 AND value IS NOT NULL)")]
internal sealed class Setting : IEntity<int>
{
    /// <summary>Surrogate row identifier; DB-assigned identity.</summary>
    [DbColumn("id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Scope discriminator (Global / Namespace / Definition). Part of the natural identity carried by
    /// the filtered unique pair <c>ux_settings_scope_name</c> / <c>ux_settings_global_name</c>.
    /// </summary>
    [DbColumn("scope_code")]
    public SettingScopeCode ScopeCode { get; init; }

    /// <summary>
    /// Target catalog row for narrowed scopes (<c>namespaces.id</c> / <c>definitions.id</c>); NULL for
    /// <c>Global</c>. No FK: the referenced catalog differs per <see cref="ScopeCode"/>.
    /// </summary>
    [DbColumn("scope_id", DbKind.Int32)]
    public int? ScopeId { get; init; }

    /// <summary>
    /// Lowercase dotted-kebab setting name (for example <c>sys.claim.batch-size</c>). ASCII Acta name.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// Format-id selector for <see cref="Value"/>; same payload-format convention as job input and
    /// variables. <c>ck_settings_value_pair</c> enforces <c>(value_format_id = 0) = (value IS NULL)</c>.
    /// </summary>
    [DbColumn("value_format_id", DbKind.Byte)]
    public byte ValueFormatId { get; set; }

    /// <summary>Encoded setting value; opaque bytes governed by <see cref="ValueFormatId"/>.</summary>
    [DbColumn("value", DbKind.BinaryPayload)]
    public byte[]? Value { get; set; }

    /// <summary>Operator-readable description of what the setting controls.</summary>
    [DbColumn("description", DbKind.UnicodeString, Size = 512)]
    public string? Description { get; set; }

    /// <summary>When the setting row was first inserted. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last-write instant. Rendered server-side on INSERT; operations bump it on UPDATE.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token; operations manually increment via <c>SET version = version + 1</c>
    /// on every UPDATE.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
