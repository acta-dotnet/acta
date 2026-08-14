namespace Acta;

/// <summary>
/// Outcome of an <see cref="ISchedules"/> control verb (Pause / Resume). Carries the coarse
/// <see cref="ControlAction"/> and, on a successful attempt, the schedule's resulting state:
/// its <see cref="Status"/>, the timed-pause expiry (<see cref="PausedUntilUtc"/>), the per-schedule
/// cursor (<see cref="NextRunAtUtc"/>), and the row <see cref="Version"/> for optimistic concurrency.
/// </summary>
/// <remarks>
/// Every field except <see cref="Action"/> is null on
/// <see cref="ControlAction.NotFound"/> and on <see cref="ControlAction.Rejected"/>.
/// </remarks>
/// <param name="Action">Whether the control verb was applied, rejected, or the schedule was absent.</param>
/// <param name="Status">The schedule's status after the attempt.</param>
/// <param name="PausedUntilUtc">When a timed pause expires; null for an indefinite pause or after resume.</param>
/// <param name="NextRunAtUtc">The schedule's cursor after the attempt.</param>
/// <param name="Version">The schedule row's optimistic-concurrency version after the attempt.</param>
public sealed record ScheduleControlResult(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version
);
