namespace Acta;

[DbProjection(ByName = true)]
internal sealed record GetScheduleHorizonRow(DateTime? NextEvaluationAtUtc);
