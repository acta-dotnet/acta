namespace Acta;

internal sealed record GetCalendarSchedulesResponse(IReadOnlyList<DueSchedule> Schedules);
