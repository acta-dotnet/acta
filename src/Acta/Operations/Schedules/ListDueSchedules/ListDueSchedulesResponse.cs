namespace Acta;

/// <summary>Every member schedule of each slot with a schedule due for evaluation, in slot then name order.</summary>
internal sealed record ListDueSchedulesResponse(IReadOnlyList<DueSchedule> Schedules);
