namespace Acta;

/// <summary>
/// An effective schedule row read for one slot. Expression and time zone are the effective values
/// (override falling back to original). <c>NextRunAtUtc</c> is the stored cursor; null means the
/// schedule has no pending occurrence. <c>BaseExpression</c>/<c>BaseTimeZone</c> are the un-overridden
/// defaults, carried so a caller clearing an override (e.g. set-overrides) can compute the resulting
/// effective value without a second read. <c>Version</c> is the row version the read observed, which a
/// fire plan hands back so its advance is refused if the row moved in the meantime.
/// </summary>
[DbProjection(ByName = true)]
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
    string BaseTimeZone,
    int Version
);

/// <summary>
/// Persisted per-schedule cursor and operator lifecycle read at startup, keyed downstream by
/// <c>(DefinitionId, ScheduleName)</c>, driving misfire-aware reconciliation against the current descriptor schedules.
/// <see cref="Status"/> and <see cref="PausedUntilUtc"/> keep a paused schedule out of the recomputed slot cursor across
/// redeploys, or, when timed, only a wake point in it; <see cref="SlotStatus"/> and <see cref="SlotNextRunAtUtc"/> are
/// the owning slot job's, the same on every row of one slot; the expression kind, expression, time zone, misfire
/// strategy, and description are the stored declaration, and an operator's <see cref="ExpressionOverride"/> matches its kind.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StoredScheduleState(
    int DefinitionId,
    string ScheduleName,
    DateTime? NextRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    JobStatusCode? SlotStatus = null,
    DateTime? SlotNextRunAtUtc = null,
    ScheduleExpressionKindCode? ExpressionKind = null,
    string? ExpressionOverride = null,
    string? TimeZoneIdOverride = null,
    string? Expression = null,
    string? TimeZoneId = null,
    MisfireStrategyCode? MisfireStrategy = null,
    string? Description = null
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
/// All schedules for one definition, grouped for a single <c>register_schedules</c> call. Carries
/// the slot's serialized default input, denormalized audit level, target status, and the slot cursor
/// (<c>SlotMinNextRunAtUtc</c> is MIN over live schedules; null means Paused, exhausted, or removed).
/// </summary>
internal sealed record DefinitionSchedules(
    int NamespaceId,
    int DefinitionId,
    string JobName,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    DefinitionAuditLevelCode AuditLevel,
    JobStatusCode SlotStatus,
    DateTime? SlotMinNextRunAtUtc,
    IReadOnlyList<SlotSchedule> Schedules
);
