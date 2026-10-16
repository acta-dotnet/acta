namespace Acta;

/// <summary>
/// A computed cursor advance for one schedule of the completing slot job, applied by <c>complete_executions</c> on a
/// recurring fire. A null <c>NextRunAtUtc</c> clears the schedule's cursor (no further occurrences).
/// <c>ExpectedVersion</c> is the schedule's version the plan was made against: the advance is applied
/// only if the row still carries it, so an operator edit or a re-registration that landed while the
/// attempt ran keeps its cursor instead of being overwritten by a plan made before it. Null applies
/// the advance unguarded, which is what a build predating the guard sends.
/// </summary>
internal sealed record ScheduleAdvance(string ScheduleName, DateTime? NextRunAtUtc, int? ExpectedVersion = null);
