using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Schedules;

/// <summary>
/// An effective schedule row read for one slot. Expression and time zone are the effective values
/// (override falling back to original). <c>NextRunAtUtc</c> is the stored cursor; null means the
/// schedule has no pending occurrence. <c>BaseExpression</c>/<c>BaseTimeZone</c> are the un-overridden
/// defaults, carried so a caller clearing an override (e.g. set-overrides) can compute the resulting
/// effective value without a second read.
/// </summary>
internal sealed record LiveSchedule(
    long Id,
    string Name,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    DateTime? NextRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    string BaseExpression,
    string BaseTimeZone
);

/// <summary>
/// Persisted per-schedule cursor and operator lifecycle read at startup, keyed downstream by
/// <c>(DefinitionId, ScheduleName)</c>. Drives misfire-aware reconciliation against the current
/// descriptor schedules; <see cref="Status"/> and <see cref="PausedUntilUtc"/> keep a paused schedule
/// out of (or, when timed, only a wake point in) the recomputed slot cursor across redeploys.
/// </summary>
internal sealed record StoredScheduleState(
    int DefinitionId,
    string ScheduleName,
    DateTime? NextRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc
);

/// <summary>
/// One <c>schedules</c> row projected for the schedules list read; expression and time zone are
/// the effective values (override when present, else original).
/// </summary>
internal sealed record JobScheduleListRow(
    long JobScheduleId,
    long JobId,
    int DefinitionId,
    string JobNamespace,
    string JobName,
    string ScheduleName,
    ScheduleOriginCode Origin,
    ScheduleExpressionKindCode ExpressionKind,
    string Expression,
    string TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    DateTime? NextRunAtUtc,
    DateTime? LastRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version,
    Guid? JobRef
)
{
    public ScheduleListItem ToItem() =>
        new(
            JobScheduleId,
            JobId,
            DefinitionId,
            JobNamespace,
            JobName,
            ScheduleName,
            Origin,
            ExpressionKind,
            Expression,
            TimeZoneId,
            MisfireStrategy,
            NextRunAtUtc,
            LastRunAtUtc,
            Status,
            PausedUntilUtc,
            CreatedAtUtc,
            ModifiedAtUtc,
            Version,
            JobRef is { } jobRef ? new JobRef(jobRef) : null
        );
}

/// <summary>
/// Result of a schedule control transition: the action plus the schedule row's state after the
/// attempt. Every control statement returns exactly one
/// <c>(action, status_code, paused_until_utc, next_run_at_utc, version)</c> row.
/// </summary>
internal sealed record ScheduleControlOutcome(
    JobControlActionInternal Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version
);

/// <summary>
/// One declared schedule's reconciled state for a definition's slot upsert. <c>NextRunAtUtc</c> is the
/// computed cursor; null means the expression has no upcoming occurrence (exhausted).
/// </summary>
internal sealed record SlotSchedule(
    string Name,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    DateTime? NextRunAtUtc
);

/// <summary>
/// All schedules for one definition, grouped for a single <c>register_scheduled_jobs</c> call. Carries
/// the slot's serialized default input, denormalized audit level, target status, and the slot cursor
/// (<c>SlotMinNextRunAtUtc</c> is MIN over live schedules; null means Paused, exhausted, or removed).
/// </summary>
internal sealed record DefinitionSchedules(
    int NamespaceId,
    int DefinitionId,
    string JobName,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    JobAuditLevelCode AuditLevel,
    JobStatusCode SlotStatus,
    DateTime? SlotMinNextRunAtUtc,
    IReadOnlyList<SlotSchedule> Schedules
);

/// <summary>
/// Definition/slot id pair returned by <c>register_scheduled_jobs</c>.
/// </summary>
internal readonly record struct RegisteredScheduleSlot(int DefinitionId, long SlotId);
