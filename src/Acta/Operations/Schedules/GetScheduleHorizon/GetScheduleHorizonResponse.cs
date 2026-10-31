namespace Acta;

/// <summary>
/// The earliest instant the namespace's engine next has anything to do, null when nothing timed is pending, and the
/// database's clock when it was read.
/// </summary>
internal sealed record GetScheduleHorizonResponse(DateTime? NextEvaluationAtUtc, DateTime DbNowUtc);
