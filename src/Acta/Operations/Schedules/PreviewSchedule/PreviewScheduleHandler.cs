namespace Acta;

/// <summary><see cref="ISchedules.PreviewAsync"/>: walks the effective expression from the database clock; no SQL of its own.</summary>
internal sealed class PreviewScheduleHandler(ResolveScheduleTargetHandler resolveScheduleTarget)
{
    public async ValueTask<PreviewScheduleResponse?> HandleAsync(PreviewScheduleQuery query, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(query.Schedule), ct) is not { } target)
        {
            return null;
        }

        var t = target.Schedule;
        var nextRuns = NextOccurrenceCalculator.Walk(t.Expression, t.TimeZoneId, t.ExpressionKind, target.NowUtc, query.Count);
        return new PreviewScheduleResponse(t.Expression, t.TimeZoneId ?? "UTC", nextRuns);
    }
}
