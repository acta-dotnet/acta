using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Substrate row carrying durable retry / result state for one step slot inside a Job.
/// Written by the <c>start_step</c> / <c>complete_step</c> operations that back
/// <c>ctx.RunStepAsync</c>. One row per <c>(JobId, Name)</c>; INSERT on first invocation
/// (<c>State = Pending</c>), UPDATE-in-place across retries, terminal transition to <c>Succeeded</c> or
/// <c>Exhausted</c>.
/// </summary>
/// <remarks>
/// The single row tracks the current retry state and the terminal result; per-attempt history is not
/// retained here. Void steps (<c>Task</c>, not <c>Task&lt;TOut&gt;</c>) carry
/// <c>ResultFormatId = 0</c> even on <c>Succeeded</c>, so <see cref="Status"/> is the success indicator,
/// not a Result-NULL inference. The step's effective retry policy is resolved live each attempt from
/// the parent <c>[Job]</c> policy plus the per-step <c>configure</c> overrides, so this row carries
/// no policy snapshot.
/// </remarks>
[DbTable("steps")]
[DbPrimaryKey(Name = "pk_steps", Columns = ["id"])]
[DbForeignKey(Name = "fk_steps_jobs", Target = typeof(Job), TargetColumn = "id", Column = "job_id", OnDelete = DbForeignKeyAction.Cascade)]
[DbUniqueIndex(Name = "ux_steps_job_name", Columns = ["job_id", "name"], Usage = "uniqueness")]
[DbCheck(
    Name = "ck_steps_result_pair",
    Sql = "(result_format_id = 0 AND result IS NULL) OR (result_format_id <> 0 AND result IS NOT NULL)"
)]
[DbCheck(Name = "ck_steps_attempt_number", Sql = "attempt_number >= 1")]
internal sealed class JobStep : IEntity<long>
{
    /// <summary>
    /// Surrogate row identifier.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Owning Job. CASCADE FK. Part of the natural identity carried by <c>ux_steps_job_name</c>.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Step slot name. Kebab-case ASCII. Part of the natural identity carried by
    /// <c>ux_steps_job_name</c>.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// <c>Pending</c> while retrying; <c>Succeeded</c> / <c>Exhausted</c> on terminal. CHECK rejects 0.
    /// </summary>
    [DbColumn("status_code")]
    public JobStepStatusCode Status { get; set; }

    /// <summary>
    /// Step attempt ordinal across retries (1-based; incremented on each failure within budget).
    /// </summary>
    [DbColumn("attempt_number", DbKind.Int16)]
    public short AttemptNumber { get; set; }

    /// <summary>
    /// When the next retry attempt is scheduled. NULL on terminal rows.
    /// </summary>
    [DbColumn("next_retry_at_utc", DbKind.UtcInstant)]
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>
    /// Machine-readable reason of the most recent failed attempt. NULL until first failure; preserved on
    /// terminal <c>Exhausted</c> rows so post-mortem reads always have the final failure context.
    /// </summary>
    [DbColumn("reason_code")]
    public JobEventReasonCode? ReasonCode { get; set; }

    /// <summary>
    /// Free-form prose paired with <see cref="ReasonCode"/>. NULL until first failure; truncated by
    /// <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("reason_message", DbKind.UnicodeString, Size = 512)]
    public string? ReasonMessage { get; set; }

    /// <summary>
    /// Format-id selector for <see cref="Result"/>; <c>0</c> means no result (void step or in-flight
    /// or exhausted). <c>ck_steps_result_pair</c> enforces
    /// <c>(result_format_id = 0) = (result IS NULL)</c>.
    /// </summary>
    [DbColumn("result_format_id", DbKind.Byte)]
    public byte ResultFormatId { get; set; }

    /// <summary>
    /// Encoded step result; opaque bytes. NULL when <see cref="ResultFormatId"/> is 0.
    /// </summary>
    [DbColumn("result", DbKind.BinaryPayload)]
    public byte[]? Result { get; set; }

    /// <summary>
    /// First-invocation instant; the wall-clock anchor for the <c>complete_step</c>
    /// <c>RetryWindow</c> predicate (<c>nextRetryAtUtc &gt; CreatedAtUtc + RetryWindow</c> exhausts the
    /// step). Rendered server-side via <see cref="DbDefault.UtcNow"/> on the initial INSERT and never
    /// updated thereafter.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Last-write instant. Advances on every <see cref="Status"/> transition or attempt update.
    /// </summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token; operations manually increment on UPDATE.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
