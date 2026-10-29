namespace Acta;

/// <summary>
/// The decision and wake state of one schedule, split from <see cref="ScheduleEntity"/> so an admission rewrites only
/// this narrow row. Keyed by the schedule's id, and created and deleted with its config row in the same transaction.
/// No foreign key ties the pair: every writer locks the config row first and the pair is deleted together, so a key
/// would only add a check to the hot advance. An orphaned schedule, and every schedule of a retired definition,
/// carries a NULL cursor and no wake.
/// </summary>
[DbTable("schedules_state")]
[DbPrimaryKey(Name = "pk_schedules_state", Columns = ["schedule_id"], Manual = true)]
// A namespace's upcoming occurrences in due order: the schedule list pages through it, and the overview's due-soon
// count and schedule lag seek it.
[DbIndex(
    Name = "ix_schedules_state_namespace_next",
    Columns = ["namespace_id", "next_occurrence_at_utc", "schedule_id"],
    Filter = "next_occurrence_at_utc IS NOT NULL",
    Usage = "dashboard"
)]
// The admission loop's seek: a namespace's schedules in the order they next need the engine, a timed pause's end and
// an evaluation retry included; a schedule with no wake (indefinitely paused, parked on a busy slot, exhausted) is
// out of the index and reached only by the event that ends its wait.
[DbIndex(
    Name = "ix_schedules_state_due",
    Columns = ["namespace_id", "next_evaluation_at_utc", "schedule_id"],
    Filter = "next_evaluation_at_utc IS NOT NULL",
    Usage = "claim_hot_path"
)]
[DbCheck(Name = "ck_schedules_state_outstanding_count", Sql = "outstanding_count >= 0")]
internal sealed class ScheduleStateEntity : IEntity<int>
{
    /// <summary>The schedule this state belongs to; the primary key, supplied by registration from the config row.</summary>
    [DbColumn("schedule_id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Immutable copy of the schedule's namespace, written once at insert so the reads that walk a namespace's
    /// schedules seek their index without joining the config row first.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// The next undecided nominal occurrence. NULL when the source offers no further occurrence: exhausted, orphaned,
    /// or its definition retired. Computed in C# by the evaluator; the SQL appliers only persist it.
    /// </summary>
    [DbColumn("next_occurrence_at_utc", DbKind.UtcInstant)]
    public DateTime? NextOccurrenceAtUtc { get; set; }

    /// <summary>
    /// When the engine next looks at this schedule: the next occurrence, a timed pause's end, or an evaluation retry.
    /// NULL while nothing timed ends the wait, which keeps the row out of <c>ix_schedules_state_due</c>; settlement,
    /// a resume, or a restart writes it back.
    /// </summary>
    [DbColumn("next_evaluation_at_utc", DbKind.UtcInstant)]
    public DateTime? NextEvaluationAtUtc { get; set; }

    /// <summary>Why the next evaluation is when it is; the engine's state, apart from the operator lifecycle.</summary>
    [DbColumn("evaluation_status_code")]
    public EvaluationStatusCode EvaluationStatus { get; set; }

    /// <summary>
    /// Unsettled roots charged to a PerOccurrence schedule; a Permanent slot's capacity is the slot, so its schedules
    /// keep 0.
    /// </summary>
    [DbColumn("outstanding_count", DbKind.Int32, Default = DbDefault.Zero)]
    public int OutstandingCount { get; set; }

    /// <summary>
    /// Optimistic-concurrency token, bumped by every write to this row. An admission plan carries the version it read
    /// and applies only while the row still holds it, so a plan made before a settlement, a resume, or another host's
    /// admission replans instead of overwriting them.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }

    /// <summary>When the row last moved. Set server-side.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }
}
