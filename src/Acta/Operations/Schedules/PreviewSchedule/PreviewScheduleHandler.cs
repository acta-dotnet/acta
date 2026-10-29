namespace Acta;

/// <summary>
/// <see cref="ISchedules.PreviewAsync"/>: walks the schedule's source from the database clock; no SQL of its own. A
/// code calendar is resolved in this process, so the host that answers needs it registered; a catalog one is read.
/// </summary>
internal sealed class PreviewScheduleHandler(ResolveScheduleTargetHandler resolveScheduleTarget, CalendarsOperations calendars)
{
    public async ValueTask<PreviewScheduleResponse?> HandleAsync(PreviewScheduleQuery query, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(query.Schedule), ct) is not { } target)
        {
            return null;
        }

        var t = target.Timing;
        var source = await calendars.SourceAsync(target.NamespaceId, t, ct);
        var nextOccurrences = NextOccurrenceCalculator.Walk(source, target.NowUtc, query.Count);
        return new PreviewScheduleResponse(t.Expression, t.TimeZoneId ?? "UTC", nextOccurrences);
    }
}
