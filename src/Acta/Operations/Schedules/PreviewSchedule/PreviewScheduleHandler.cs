namespace Acta;

/// <summary>
/// <see cref="ISchedules.PreviewAsync"/>: walks the schedule's source from the database clock; no SQL of its own. A
/// calendar it names is read from the catalog.
/// </summary>
internal sealed class PreviewScheduleHandler(GetScheduleTargetHandler getScheduleTarget, CalendarsOperations calendars)
{
    public async ValueTask<PreviewScheduleResponse?> HandleAsync(PreviewScheduleQuery query, CancellationToken ct)
    {
        if (await getScheduleTarget.HandleAsync(new GetScheduleTargetQuery(query.Schedule), ct) is not { } target)
        {
            return null;
        }

        var t = target.Timing;
        var source = await calendars.SourceAsync(target.NamespaceId, t, ct);
        var nextOccurrences = NextOccurrenceCalculator.Walk(source, target.NowUtc, query.Count);
        return new PreviewScheduleResponse(t.Expression, t.TimeZoneId ?? "UTC", nextOccurrences);
    }
}
