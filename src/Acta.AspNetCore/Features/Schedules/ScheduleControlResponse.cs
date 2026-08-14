namespace Acta.AspNetCore.Features.Schedules;

/// <summary>
/// HTTP projection of a <see cref="ScheduleControlResult"/> with an operator-readable message.
/// Returned for applied (200), rejected (409), and not-found (404) outcomes alike.
/// </summary>
internal sealed record ScheduleControlResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version,
    string Message
);
