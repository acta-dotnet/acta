namespace Acta;

/// <summary>
/// One durable result per job attempt, keyed by <c>(JobId, ExecutionNumber)</c>, so result bytes stay off the hot
/// job row and a recurring job keeps a history instead of overwriting one slot. <c>complete_execution</c> trims a
/// recurring job's rows to the definition's <c>RecurringResultCap</c> (default 1) on every completion, and a deleted
/// job takes its rows with it (<c>fk_results_jobs ON DELETE CASCADE</c>). The latest result is one primary-key seek.
/// </summary>
// No page compression: every completion appends here, and compressing a full tail page holds the latch the other
// finalizers wait on; dropping it gained about 4% of SQL Server drain at 16 workers.
[DbTable("results")]
// OptimizeForSequentialKey: results arrive by ascending job id, so they insert at this key's tail; a
// per-index page-latch attribution measured 17 s of wait on that tail page at 16 workers.
[DbPrimaryKey(Name = "pk_results", Columns = ["job_id", "execution_number"], OptimizeForSequentialKey = true)]
[DbForeignKey(
    Name = "fk_results_jobs",
    Target = typeof(JobEntity),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbCheck(Name = "ck_results_format_not_none", Sql = "result_format_id <> 0")]
internal sealed class ResultEntity : IEntity
{
    /// <summary>
    /// Owning Job. Part of the composite clustered PK with <see cref="ExecutionNumber"/>; the FK cascades
    /// from <c>acta.jobs</c> so a Job retention-delete sweeps every result row for it.
    /// bigint.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Which attempt produced this result. Part of the composite clustered PK; together with
    /// <see cref="JobId"/> uniquely identifies the result row across the Job's history of attempts. The
    /// value matches the <c>JobEvent.ExecutionNumber</c> on the corresponding <c>job.execution-finished</c>
    /// event.
    /// int.
    /// </summary>
    [DbColumn("execution_number", DbKind.Int32)]
    public int ExecutionNumber { get; init; }

    /// <summary>
    /// Format-id selector for <see cref="Result"/>. The source of truth for the result's payload format.
    /// tinyint.
    /// </summary>
    [DbColumn("result_format_id", DbKind.Byte)]
    public byte ResultFormatId { get; init; }

    /// <summary>
    /// Encoded result payload; opaque bytes whose format is governed by <see cref="ResultFormatId"/>.
    /// </summary>
    [DbColumn("result", DbKind.BinaryPayload)]
    public byte[] Result { get; init; } = [];

    /// <summary>
    /// When the row was inserted; rendered server-side via <see cref="DbDefault.UtcNow"/> in the same
    /// transaction as the <c>job.execution-finished</c> event. The operation does not supply this value
    /// from C#.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }
}
