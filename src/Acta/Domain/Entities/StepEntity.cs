namespace Acta;

/// <summary>
/// The durable outcome of one step, one row per <c>(JobId, StepName)</c>, written by <c>start_job_step</c> and
/// <c>complete_job_step</c> behind <c>ctx.RunStepAsync</c>: inserted Pending on first run and ended Succeeded, or, for
/// an at-most-once step, Exhausted or Interrupted (a job restart deletes an Exhausted row). <see cref="Status"/> is
/// the success indicator, since a void step stores no result. A step retries through its job, so the row counts no
/// attempts. Keyed by its job, as every job satellite is: concurrent steps of in-flight jobs spread over those jobs' pages.
/// </summary>
[DbTable("steps")]
[DbPrimaryKey(Name = "pk_steps", Columns = ["job_id", "step_name"])]
[DbCheck(
    Name = "ck_steps_result_pair",
    Sql = "(result_format_id = 0 AND result IS NULL) OR (result_format_id <> 0 AND result IS NOT NULL)"
)]
[DbCheck(Name = "ck_steps_result_succeeded", Sql = "result_format_id = 0 OR step_status_code = 100")]
internal sealed class StepEntity : IEntity
{
    /// <summary>
    /// Owning Job; purge and retention delete a job's steps with it. The key's first column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Step name. Kebab-case ASCII. The key's second column.
    /// </summary>
    [DbColumn("step_name", DbKind.AsciiString, Size = 128)]
    public string StepName { get; init; } = default!;

    /// <summary>
    /// <c>Pending</c> until the step succeeds; <c>Succeeded</c>, <c>Exhausted</c>, or <c>Interrupted</c> on terminal.
    /// </summary>
    [DbColumn("step_status_code")]
    public StepStatusCode Status { get; set; }

    /// <summary>
    /// Operator-readable failure of an at-most-once step, set on its terminal <c>Exhausted</c> or <c>Interrupted</c>
    /// row and NULL on every other row; truncated by <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("failure_message", DbKind.UnicodeString, Size = 512)]
    public string? FailureMessage { get; set; }

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
    /// First-invocation instant. Rendered server-side via <see cref="DbDefault.UtcNow"/> on the initial INSERT and
    /// never updated thereafter.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Last-write instant. Advances on every <see cref="Status"/> transition.
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
