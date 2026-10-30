namespace Acta;

/// <summary>What the apply answers: the action and the version current when it looked.</summary>
[DbProjection(ByName = true)]
internal sealed record PublishCalendarRow(ControlAction Action, int? CurrentVersion);
