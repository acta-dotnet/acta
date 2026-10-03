namespace Acta.Runtime;

/// <summary><see cref="ISchedules.PreviewAsync"/>: walks the effective expression from the database clock; no SQL of its own.</summary>
internal sealed class PreviewScheduleHandler(ScheduleTargetResolution targets)
{
    public async ValueTask<SchedulePreview?> HandleAsync(PreviewScheduleQuery query, CancellationToken ct)
    {
        if (await targets.ResolveAsync(query.Schedule, ct) is not { } target)
        {
            return null;
        }

        var t = target.Schedule;
        var nextRuns = NextOccurrenceCalculator.Walk(t.Expression, t.TimeZoneId, t.ExpressionKind, target.NowUtc, query.Count);
        return new SchedulePreview(t.Expression, t.TimeZoneId ?? "UTC", nextRuns);
    }
}
