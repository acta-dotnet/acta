namespace Acta;

/// <summary>
/// The durable retry and result state of one step, one row per <c>(JobId, Name)</c>, written by <c>start_step</c>
/// and <c>complete_step</c> behind <c>ctx.RunStepAsync</c>: inserted Pending on first run, updated across retries,
/// and ended Succeeded, Exhausted, or Interrupted (a job restart deletes an Exhausted row). <see cref="Status"/> is
/// the success indicator, since a void step stores no result, and the retry policy is resolved live each attempt.
/// Keyed by its job, as every job satellite is: concurrent steps of in-flight jobs spread over those jobs' pages.
/// </summary>
[DbTable("steps")]
[DbPrimaryKey(Name = "pk_steps", Columns = ["job_id", "name"])]
[DbForeignKey(
    Name = "fk_steps_jobs",
    Target = typeof(JobEntity),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbCheck(
    Name = "ck_steps_result_pair",
    Sql = "(result_format_id = 0 AND result IS NULL) OR (result_format_id <> 0 AND result IS NOT NULL)"
)]
[DbCheck(Name = "ck_steps_attempt_number", Sql = "attempt_number >= 1")]
[DbCheck(Name = "ck_steps_terminal_no_retry", Sql = "status_code NOT IN (100, 200, 230) OR next_retry_at_utc IS NULL")]
[DbCheck(Name = "ck_steps_result_succeeded", Sql = "result_format_id = 0 OR status_code = 100")]
[DbCheck(Name = "ck_steps_reason_pair", Sql = "reason_message IS NULL OR reason_code IS NOT NULL")]
internal sealed class StepEntity : IEntity
{
    /// <summary>
    /// Owning Job. CASCADE FK. The key's first column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Step slot name. Kebab-case ASCII. The key's second column.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// <c>Pending</c> while retrying; <c>Succeeded</c> / <c>Exhausted</c> on terminal. CHECK rejects 0.
    /// </summary>
    [DbColumn("status_code")]
    public StepStatusCode Status { get; set; }

    /// <summary>
    /// Step attempt ordinal across retries (1-based; incremented on each failure within budget).
    /// </summary>
    [DbColumn("attempt_number", DbKind.Int32)]
    public int AttemptNumber { get; set; }

    /// <summary>
    /// When the next retry attempt is scheduled. <c>ck_steps_terminal_no_retry</c> keeps it NULL on
    /// terminal rows.
    /// </summary>
    [DbColumn("next_retry_at_utc", DbKind.UtcInstant)]
    public DateTime? NextRetryAtUtc { get; set; }

    /// <summary>
    /// Machine-readable reason of the most recent failed attempt. NULL until first failure, and cleared
    /// again by a succeeding attempt; preserved on terminal <c>Exhausted</c> and <c>Interrupted</c> rows
    /// so post-mortem reads always have the final failure context.
    /// </summary>
    [DbColumn("reason_code")]
    public EventReasonCode? ReasonCode { get; set; }

    /// <summary>
    /// Free-form prose paired with <see cref="ReasonCode"/> by <c>ck_steps_reason_pair</c>. NULL until
    /// first failure; truncated by <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("reason_message", DbKind.UnicodeString, Size = 512)]
    public string? ReasonMessage { get; set; }

    /// <summary>
    /// Format-id selector for <see cref="Result"/>; <c>0</c> means no result (void step or in-flight
    /// or exhausted). <c>ck_steps_result_pair</c> enforces
    /// <c>(result_format_id = 0) = (result IS NULL)</c>, and <c>ck_steps_result_succeeded</c> admits a
    /// non-zero id only on a <c>Succeeded</c> row.
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
