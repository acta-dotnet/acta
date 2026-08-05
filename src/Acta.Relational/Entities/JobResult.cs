using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Cold payload table: one row per Job attempt that produced a durable result, keyed by the composite
/// <c>(JobId, ExecutionNumber)</c>. Result bytes never live on the hot <c>Job</c> row, and recurring Jobs
/// accumulate one cold row per terminal firing instead of overwriting a single hot LOB slot. The latest
/// retained result for a Job is a single-row seek against the clustered PK
/// (<c>WHERE JobId = @id ORDER BY ExecutionNumber DESC</c>).
/// </summary>
/// <remarks>
/// Retention cascades from <c>Job</c>: <c>fk_results_jobs ON DELETE CASCADE</c> sweeps every result row
/// when its parent Job is retention-deleted, so there is no per-row retention column. <c>JobEvent</c>
/// records the <c>job.execution-finished</c> event whose <c>JobId + ExecutionNumber</c> pair points at
/// this row; the event ledger has its own retention.
/// </remarks>
[DbTable("results", PageCompression = true)]
[DbPrimaryKey(Name = "pk_results", Columns = ["job_id", "execution_number"])]
[DbForeignKey(
    Name = "fk_results_jobs",
    Target = typeof(Job),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbCheck(Name = "ck_results_format_not_none", Sql = "result_format_id <> 0")]
internal sealed class JobResult : IEntity
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
