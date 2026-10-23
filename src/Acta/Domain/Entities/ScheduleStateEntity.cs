namespace Acta;

/// <summary>
/// The cursor of one schedule, split from <see cref="ScheduleEntity"/> so the per-occurrence advance rewrites only
/// this narrow row. Keyed like its config row, and created and deleted with it in the same transaction. No foreign
/// key ties the pair: a key here would have to be composite, and the schema model declares single-column keys only.
/// An orphaned schedule, and every schedule of a retired definition, carries a NULL cursor, which keeps it out of
/// <c>ix_schedules_state_namespace_next</c>.
/// </summary>
[DbTable("schedules_state")]
[DbPrimaryKey(Name = "pk_schedules_state", Columns = ["job_id", "schedule_name"])]
// A namespace's upcoming occurrences in due order: the schedule list pages through it, and the overview's due-soon
// count and schedule lag seek it.
[DbIndex(
    Name = "ix_schedules_state_namespace_next",
    Columns = ["namespace_id", "next_occurrence_at_utc", "job_id", "schedule_name"],
    Filter = "next_occurrence_at_utc IS NOT NULL",
    Usage = "dashboard"
)]
internal sealed class ScheduleStateEntity : IEntity
{
    /// <summary>The slot Job of the schedule; the key's first column.</summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>The schedule's name; the key's second column.</summary>
    [DbColumn("schedule_name", DbKind.AsciiString, Size = 128)]
    public string ScheduleName { get; init; } = default!;

    /// <summary>
    /// Immutable copy of the schedule's namespace, written once at insert so the reads that walk a namespace's
    /// upcoming occurrences seek <c>ix_schedules_state_namespace_next</c> without joining the config row first.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// The next instant this schedule is due; the slot Job's <c>next_run_at_utc</c> is the MIN over its live
    /// schedules. NULL when the schedule offers no further occurrence: exhausted, orphaned, or its definition retired.
    /// Computed in C# (<c>NextOccurrenceCalculator</c> / <c>ScheduleWalker</c>); the SQL appliers only persist it.
    /// </summary>
    [DbColumn("next_occurrence_at_utc", DbKind.UtcInstant)]
    public DateTime? NextOccurrenceAtUtc { get; set; }

    /// <summary>
    /// The most recent occurrence this schedule was advanced past, set to the outgoing
    /// <see cref="NextOccurrenceAtUtc"/> on every advance. An occurrence, not a run: the cursor also moves when the
    /// misfire policy skips, so this records the latest schedule point crossed rather than a completed execution. NULL
    /// until the schedule first advances, and again once it is orphaned or its definition retired. The only
    /// per-schedule history when several schedules share one recurring slot Job.
    /// </summary>
    [DbColumn("last_advanced_occurrence_at_utc", DbKind.UtcInstant)]
    public DateTime? LastAdvancedOccurrenceAtUtc { get; set; }

    /// <summary>When the cursor last moved. Set server-side.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }
}
