using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Schedules;

/// <summary>
/// Persistence port for recurring schedules: the slot-scoped live read, the namespace-scoped stored
/// cursor read, the paged operator list, the startup slot/schedule reconcile upsert, and the operator
/// control transitions (pause / resume / overrides / manual trigger). Every control write applies the
/// caller-computed cursor recompute to the schedule row and its owning slot job in one transaction.
/// </summary>
internal interface IScheduleStore
{
    /// <summary>
    /// Effective (non-orphaned) schedules for one recurring slot, with override-or-original expression
    /// and time zone resolved. Read once per recurring fire for the due-set and recompute.
    /// </summary>
    Task<IReadOnlyList<LiveSchedule>> GetLiveSchedulesAsync(long jobId, CancellationToken ct);

    /// <summary>
    /// Persisted (non-orphaned) per-schedule cursors for the namespace. Drives startup misfire-aware
    /// reconciliation and the slot-cancel decision for definitions that dropped every schedule.
    /// </summary>
    Task<IReadOnlyList<StoredScheduleState>> GetScheduleStateAsync(short namespaceId, CancellationToken ct);

    /// <summary>
    /// One keyset page of schedule rows ordered <c>next_run_at_utc ASC, id ASC</c> plus the opt-in
    /// filter-wide total, fetched in one round trip as two result sets.
    /// </summary>
    Task<SchedulePage> ListJobSchedulesAsync(SchedulePageRequest request, CancellationToken ct);

    /// <summary>
    /// Registers every definition's recurring slot job and its schedules in one set-based call
    /// (ensures each slot job, refreshes input/audit/status/cursor, upserts schedules, orphan-sweeps).
    /// Returns one <see cref="RegisteredScheduleSlot"/> per definition.
    /// </summary>
    Task<IReadOnlyList<RegisteredScheduleSlot>> RegisterScheduledJobsAsync(RegisterScheduledJobsCommand command, CancellationToken ct);

    /// <summary>
    /// Pause one named schedule (Status to Paused) and apply the caller-recomputed slot cursor. The
    /// schedule's own cursor is left untouched. Emits <c>schedule.paused</c> against the slot job.
    /// </summary>
    Task<ScheduleControlOutcome> PauseScheduleAsync(PauseScheduleCommand command, CancellationToken ct);

    /// <summary>
    /// Resume one named schedule (Status to Active, clearing any pause), set its caller-reconciled
    /// cursor, and apply the recomputed slot cursor. Emits <c>schedule.resumed</c> against the slot job.
    /// </summary>
    Task<ScheduleControlOutcome> ResumeScheduleAsync(ResumeScheduleCommand command, CancellationToken ct);

    /// <summary>
    /// Applies a full-set operator override to one named schedule's expression and/or time zone,
    /// CAS-guarded on <c>version</c>; a stale expected version is rejected with current state. Persists
    /// the caller-recomputed schedule cursor and slot cursor in the same two-table write.
    /// </summary>
    Task<ScheduleControlOutcome> SetScheduleOverridesAsync(SetScheduleOverridesCommand command, CancellationToken ct);

    /// <summary>
    /// Manually fire a named schedule's owning recurring slot right now by pulling the slot cursor to
    /// the current instant. Paused, in-flight, and missing schedules are rejected in SQL.
    /// </summary>
    Task<ScheduleControlOutcome> TriggerScheduleNowAsync(TriggerScheduleNowCommand command, CancellationToken ct);
}

/// <summary>Decoded schedules list request; <c>Take</c> carries the page-size-plus-one peek-ahead.</summary>
internal sealed record SchedulePageRequest(
    string? JobNamespace,
    string? JobName,
    ScheduleOriginCode? Origin,
    bool? LiveOnly,
    DateTime? CursorNextRunAtUtc,
    long? CursorId,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of mapped schedule list items plus the opt-in filtered total.</summary>
internal sealed record SchedulePage(IReadOnlyList<ScheduleListItem> Rows, long? Total);

/// <summary>
/// Validated startup reconcile upsert: all definitions share one namespace. <c>SlotRefs</c> is
/// positionally aligned with <c>Definitions</c>: a C#-allocated public ref per slot, used only when
/// the slot job row is freshly inserted (an existing slot keeps its stored ref).
/// </summary>
internal sealed record RegisterScheduledJobsCommand(IReadOnlyList<DefinitionSchedules> Definitions, IReadOnlyList<Guid> SlotRefs);

/// <summary>Validated pause: a null <c>PausedUntilUtc</c> is indefinite; a timestamp is a timed pause.</summary>
internal sealed record PauseScheduleCommand(
    long JobId,
    string ScheduleName,
    DateTime? PausedUntilUtc,
    DateTime? JobNextRunAtUtc,
    JobControlActor Actor,
    string? Note
);

/// <summary>Validated resume; <c>ScheduleNextRunAtUtc</c> is the C#-reconciled per-schedule cursor.</summary>
internal sealed record ResumeScheduleCommand(
    long JobId,
    string ScheduleName,
    DateTime? ScheduleNextRunAtUtc,
    DateTime? JobNextRunAtUtc,
    JobControlActor Actor,
    string? Note
);

/// <summary>
/// Validated override set: a null <c>Expression</c> / <c>TimeZoneId</c> clears that override (falls
/// back to the definition default). Cursors are recomputed by the caller under the new effective
/// expression.
/// </summary>
internal sealed record SetScheduleOverridesCommand(
    long JobId,
    string ScheduleName,
    int ExpectedVersion,
    string? Expression,
    string? TimeZoneId,
    string? Note,
    DateTime? ScheduleNextRunAtUtc,
    DateTime? JobNextRunAtUtc,
    JobControlActor Actor,
    string? ReasonMessage
);

/// <summary>Validated manual fire; <c>ReasonMessage</c> is the schedule name plus any operator note.</summary>
internal sealed record TriggerScheduleNowCommand(long JobId, string ScheduleName, JobControlActor Actor, string ReasonMessage);
