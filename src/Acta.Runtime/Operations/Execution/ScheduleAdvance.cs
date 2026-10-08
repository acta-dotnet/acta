namespace Acta.Runtime;

/// <summary>
/// A computed cursor advance for one schedule, applied by <c>complete_execution</c> on a recurring fire.
/// A null <c>NextRunAtUtc</c> clears the schedule's cursor (no further occurrences).
/// <c>ExpectedVersion</c> is the schedule's version the plan was made against: the advance is applied
/// only if the row still carries it, so an operator edit or a re-registration that landed while the
/// attempt ran keeps its cursor instead of being overwritten by a plan made before it. Null applies
/// the advance unguarded, which is what a build predating the guard sends.
/// </summary>
internal sealed record ScheduleAdvance(long ScheduleId, DateTime? NextRunAtUtc, int? ExpectedVersion = null);
