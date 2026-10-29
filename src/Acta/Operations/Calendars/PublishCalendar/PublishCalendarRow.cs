namespace Acta;

/// <summary>What the apply answers: the action, how many schedules it retimed, and the version current when it looked.</summary>
[DbProjection(ByName = true)]
internal sealed record PublishCalendarRow(ControlAction Action, int Affected, int? CurrentVersion);
