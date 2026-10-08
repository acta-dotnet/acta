namespace Acta;

/// <summary>The effective schedules of one recurring slot.</summary>
internal sealed record GetLiveSchedulesResponse(IReadOnlyList<LiveSchedule> Schedules);
