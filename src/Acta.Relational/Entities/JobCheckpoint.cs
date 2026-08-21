using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One durable substrate slot per <c>(JobId, Kind, Name)</c> in the merged <c>checkpoints</c> table:
/// user variables, signals, sleep timers, the progress slot, and child terminal-outcome latches all
/// share this one physical shape, discriminated by <see cref="Kind"/>. Steps are richer
/// (attempt counters, retry budget) and stay in the separate <c>steps</c> table.
/// </summary>
/// <remarks>
/// Variables and progress are stateless UPSERT slots (last-writer-wins, <see cref="Status"/> NULL).
/// Signals and child latches move <c>Pending</c> to <c>Set</c>, or to <c>Expired</c> when a bounded
/// wait outlives <see cref="DueAtUtc"/>; timers move <c>Pending</c> to <c>Consumed</c>. The composite
/// primary key is the natural identity; there is no surrogate id. This is Job-internal substrate,
/// not an audited entity; the lifecycle audit trail lives in <c>JobEvent</c>.
/// </remarks>
[DbTable("checkpoints")]
[DbPrimaryKey(Name = "pk_checkpoints", Columns = ["job_id", "kind_code", "name"])]
[DbForeignKey(
    Name = "fk_checkpoints_jobs",
    Target = typeof(Job),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbCheck(
    Name = "ck_checkpoints_value_pair",
    Sql = "(value_format_id = 0 AND value IS NULL) OR (value_format_id <> 0 AND value IS NOT NULL)"
)]
internal sealed class JobCheckpoint : IEntity
{
    /// <summary>
    /// Owning Job. CASCADE FK: when the Job is purged, every checkpoint row for it cascades away in
    /// the same transaction. Leading column of the composite PK.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Which substrate feature owns this slot (variable / signal / timer / progress / child-latch).
    /// Part of the composite PK, so identical names under different kinds never collide.
    /// </summary>
    [DbColumn("kind_code")]
    public JobCheckpointKindCode Kind { get; init; }

    /// <summary>
    /// Slot name. Dotted-kebab ASCII for user variables and signals; <c>sys.progress</c> for the
    /// progress slot; <c>sys.child.{childId}</c> for child latches. The <c>sys.</c> prefix stays
    /// system-reserved for user-writable kinds. Part of the composite PK.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// <c>Pending</c> / <c>Set</c> / <c>Expired</c> (signals, child latches) or <c>Pending</c> /
    /// <c>Consumed</c> (timers). NULL for the stateless kinds (variable, progress).
    /// </summary>
    [DbColumn("status_code")]
    public JobCheckpointStatusCode? Status { get; set; }

    /// <summary>
    /// The named wait's absolute expiration: a timer's due instant, or a bounded signal / child wait's
    /// deadline (NULL on an unbounded wait and on every stateless kind). Written once when the slot is
    /// armed and never extended, so a replay reuses it. Distinct from the Job's <c>next_run_at_utc</c>,
    /// which is the job-level claimability cache derived from it on suspend.
    /// </summary>
    [DbColumn("due_at_utc", DbKind.UtcInstant)]
    public DateTime? DueAtUtc { get; init; }

    /// <summary>
    /// Format-id selector for <see cref="Value"/>. <c>0</c> means no payload (pending signal, timer,
    /// presence-only raise), and is the server default so payload-free kinds omit the pair;
    /// <c>ck_checkpoints_value_pair</c> enforces <c>(value_format_id = 0) = (value IS NULL)</c>.
    /// </summary>
    [DbColumn("value_format_id", DbKind.Byte, Default = DbDefault.Zero)]
    public byte ValueFormatId { get; set; }

    /// <summary>Encoded slot payload; opaque bytes governed by <see cref="ValueFormatId"/>.</summary>
    [DbColumn("value", DbKind.BinaryPayload)]
    public byte[]? Value { get; set; }

    /// <summary>First-write instant. Rendered server-side via <see cref="DbDefault.UtcNow"/> on INSERT.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Last write of any kind (state transition, value overwrite); operations bump on every UPDATE.</summary>
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
