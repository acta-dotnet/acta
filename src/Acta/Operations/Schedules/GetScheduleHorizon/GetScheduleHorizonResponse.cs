namespace Acta;

/// <summary>The earliest instant the namespace's engine next has anything to do; null when nothing timed is pending.</summary>
internal sealed record GetScheduleHorizonResponse(DateTime? NextEvaluationAtUtc);
