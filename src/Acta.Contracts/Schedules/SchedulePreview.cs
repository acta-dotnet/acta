namespace Acta;

/// <summary>
/// A read-only forecast of a schedule's upcoming fire instants, computed live (no persisted state is
/// read or written). <see cref="Expression"/> and <see cref="TimeZoneId"/> are the effective values
/// (operator override when present, else the original); <see cref="NextRunsUtc"/> is strictly
/// increasing and may be shorter than requested when the expression is exhausted.
/// </summary>
public sealed record SchedulePreview(string Expression, string TimeZoneId, IReadOnlyList<DateTime> NextRunsUtc);
