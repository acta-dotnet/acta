namespace Acta;

/// <summary>
/// The one evaluator every scheduling path shares: given a schedule's timing, policy, cursor, and the capacity the
/// admitter read, decides at the instant it is given what to admit, what to skip, where the cursor moves, and when
/// to look again. Pure: no clock, no database, no provider call. Occurrences are nominal instants; a missed one is
/// decided by the misfire policy and never shifted.
/// </summary>
internal static class ScheduleEvaluator
{
    /// <summary>How long an evaluation error waits before the same cursor is evaluated again.</summary>
    internal static readonly TimeSpan ErrorRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The most due occurrences one evaluation walks before it gives up on the source: a cron a second behind by more
    /// than this is an error to look at, not a range to skip silently.
    /// </summary>
    private const int MaxDueWalk = 1_000_000;

    public static ScheduleEvaluation Evaluate(
        ScheduleTiming timing,
        MisfireStrategyCode misfire,
        TimeSpan grace,
        ScheduleCursor cursor,
        AdmissionCapacity capacity,
        DateTime nowUtc
    )
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        // An indefinite pause has no next evaluation; a timed pause ahead wakes at its end; an elapsed one resumes as
        // of the instant it ended, its window decided like downtime by the misfire policy.
        var pauseEnded = false;
        var downtimeEndUtc = DateTime.MinValue;
        if (cursor.Status == ScheduleStatusCode.Paused)
        {
            if (cursor.PausedUntilUtc is not { } until)
            {
                return Unchanged(cursor, EvaluationStatusCode.Scheduled, null, pauseEnded: false);
            }

            if (until > now)
            {
                return Unchanged(cursor, EvaluationStatusCode.Scheduled, until, pauseEnded: false);
            }

            pauseEnded = true;
            downtimeEndUtc = until;
        }
        else if (cursor.Status != ScheduleStatusCode.Active)
        {
            return Unchanged(cursor, EvaluationStatusCode.Scheduled, null, pauseEnded: false);
        }

        if (cursor.NextOccurrenceAtUtc is not { } next)
        {
            return Unchanged(cursor, EvaluationStatusCode.Exhausted, null, pauseEnded);
        }

        next = DateTime.SpecifyKind(next, DateTimeKind.Utc);
        if (next > now)
        {
            return Unchanged(cursor, EvaluationStatusCode.Scheduled, next, pauseEnded);
        }

        if (capacity != AdmissionCapacity.Available)
        {
            var parked =
                capacity == AdmissionCapacity.Full ? EvaluationStatusCode.WaitingForCapacity : EvaluationStatusCode.SlotUnavailable;
            return Unchanged(cursor, parked, null, pauseEnded);
        }

        DateTime latest;
        DateTime? following;
        try
        {
            latest = LatestDue(timing, next, now);
            following = NextOccurrenceCalculator.FirstAfter(
                timing.Expression,
                timing.TimeZoneId,
                timing.ExpressionKind,
                timing.IntervalAnchorAtUtc ?? latest,
                now
            );
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException)
        {
            return Unchanged(cursor, EvaluationStatusCode.EvaluationError, now + ErrorRetryDelay, pauseEnded);
        }

        // The window a pause or downtime covered holds missed occurrences; a due one since then is on time within the
        // grace, and the grace is what keeps a slow pass from counting as a misfire.
        var onTime = latest >= downtimeEndUtc && latest >= now - grace;
        AdmittedOccurrence? admit;
        DateTime? skippedFrom = null;
        DateTime? skippedThrough = null;
        switch (misfire)
        {
            case MisfireStrategyCode.CatchUpOnce:
                // One firing stands for every due occurrence: the earliest is the nominal one, the latest the boundary.
                admit = new AdmittedOccurrence(next, latest);
                break;
            case MisfireStrategyCode.Skip:
                admit = onTime ? new AdmittedOccurrence(latest, latest) : null;
                if (next < latest || !onTime)
                {
                    skippedFrom = next;
                    skippedThrough = onTime ? PreviousDue(timing, next, latest) : latest;
                }
                break;
            default:
                throw new NotSupportedException($"Misfire strategy {misfire} is not supported by this build.");
        }

        return new ScheduleEvaluation(
            admit,
            skippedFrom,
            skippedThrough,
            following,
            latest,
            following,
            following is null ? EvaluationStatusCode.Exhausted : EvaluationStatusCode.Scheduled,
            pauseEnded
        );
    }

    private static ScheduleEvaluation Unchanged(
        ScheduleCursor cursor,
        EvaluationStatusCode status,
        DateTime? nextEvaluationAtUtc,
        bool pauseEnded
    ) => new(null, null, null, cursor.NextOccurrenceAtUtc, cursor.DecidedThroughAtUtc, nextEvaluationAtUtc, status, pauseEnded);

    /// <summary>The last occurrence at or before <paramref name="nowUtc"/>, starting from the due <paramref name="fromUtc"/>.</summary>
    private static DateTime LatestDue(ScheduleTiming timing, DateTime fromUtc, DateTime nowUtc)
    {
        if (timing.ExpressionKind == ScheduleExpressionKindCode.Interval)
        {
            var interval = NextOccurrenceCalculator.ParseInterval(timing.Expression);
            var anchor = DateTime.SpecifyKind(timing.IntervalAnchorAtUtc ?? fromUtc, DateTimeKind.Utc);
            var steps = (long)Math.Floor((nowUtc - anchor) / interval);
            var latest = anchor.AddTicks(interval.Ticks * steps);
            return latest < fromUtc ? fromUtc : latest;
        }

        var current = fromUtc;
        for (var walked = 0; walked < MaxDueWalk; walked++)
        {
            if (
                NextOccurrenceCalculator.Next(timing.Expression, timing.TimeZoneId, timing.ExpressionKind, current) is not { } candidate
                || candidate > nowUtc
            )
            {
                return current;
            }

            current = candidate;
        }

        throw new InvalidOperationException(
            $"More than {MaxDueWalk} occurrences of '{timing.Expression}' are due; the source cannot be evaluated."
        );
    }

    /// <summary>The occurrence just before <paramref name="latestUtc"/>, walking from <paramref name="fromUtc"/>; the end of a skipped range.</summary>
    private static DateTime PreviousDue(ScheduleTiming timing, DateTime fromUtc, DateTime latestUtc)
    {
        if (timing.ExpressionKind == ScheduleExpressionKindCode.Interval)
        {
            return latestUtc - NextOccurrenceCalculator.ParseInterval(timing.Expression);
        }

        var current = fromUtc;
        while (
            NextOccurrenceCalculator.Next(timing.Expression, timing.TimeZoneId, timing.ExpressionKind, current) is { } candidate
            && candidate < latestUtc
        )
        {
            current = candidate;
        }

        return current;
    }
}
