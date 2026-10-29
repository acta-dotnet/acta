namespace Acta;

/// <summary>
/// The one evaluator every scheduling path shares: given a schedule's timing, policy, cursor, and the capacity the
/// admitter read, decides at the instant it is given what to admit, what to skip or expire, where the cursor moves,
/// and when to look again. Pure: no clock, no database, no provider call. Occurrences are nominal instants; a missed
/// one is decided by the misfire policy and never shifted.
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
        SchedulePolicy policy,
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
        DateTime? expiredFrom = null;
        DateTime? expiredThrough = null;
        var pausedByExpiry = false;
        try
        {
            latest = LatestDue(timing, next, now);

            // Expiration comes before the misfire policy: every due occurrence that aged past the expiry is decided
            // without work, and under Pause the schedule then stops for an operator. An operator's resume and a timed
            // pause's end expire the stale backlog once without pausing again, so the schedule can move on.
            if (policy.ExpireAfter is { } age && next <= now - age)
            {
                expiredFrom = next;
                expiredThrough = LatestDue(timing, next, now - age);
                pausedByExpiry =
                    policy.ExpireAction == ScheduleExpireActionCode.Pause
                    && !pauseEnded
                    && cursor.EvaluationStatus != EvaluationStatusCode.Resumed;
                var remaining = NextAfter(timing, expiredThrough.Value);
                if (remaining is null || remaining > now)
                {
                    return new ScheduleEvaluation(
                        null,
                        null,
                        null,
                        expiredFrom,
                        expiredThrough,
                        remaining,
                        pausedByExpiry ? null : remaining,
                        remaining is null ? EvaluationStatusCode.Exhausted : EvaluationStatusCode.Scheduled,
                        pauseEnded,
                        pausedByExpiry
                    );
                }

                next = remaining.Value;
            }

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
        var onTime = latest >= downtimeEndUtc && latest >= now - policy.Grace;
        AdmittedOccurrence? admit;
        DateTime? skippedFrom = null;
        DateTime? skippedThrough = null;
        switch (policy.Misfire)
        {
            case MisfireStrategyCode.CatchUpOnce:
                // One firing stands for every due occurrence: the earliest is the nominal one, the latest the boundary.
                admit = new AdmittedOccurrence(next, latest);
                break;
            case MisfireStrategyCode.CatchUpLatest:
                // The latest due occurrence fires however late it is; the earlier ones are coalesced into it as a skip.
                admit = new AdmittedOccurrence(latest, latest);
                if (next < latest)
                {
                    skippedFrom = next;
                    skippedThrough = PreviousDue(timing, next, latest);
                }
                break;
            case MisfireStrategyCode.CatchUpAll:
                // The earliest due occurrence fires alone; the rest of the backlog is due again at once, so the next pass
                // comes straight back and admits the next one as capacity allows.
                admit = new AdmittedOccurrence(next, next);
                following = NextAfter(timing, next);
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
                throw new NotSupportedException($"Misfire strategy {policy.Misfire} is not supported by this build.");
        }

        return new ScheduleEvaluation(
            admit,
            skippedFrom,
            skippedThrough,
            expiredFrom,
            expiredThrough,
            following,
            pausedByExpiry ? null : following,
            following is null ? EvaluationStatusCode.Exhausted : EvaluationStatusCode.Scheduled,
            pauseEnded,
            pausedByExpiry
        );
    }

    private static ScheduleEvaluation Unchanged(
        ScheduleCursor cursor,
        EvaluationStatusCode status,
        DateTime? nextEvaluationAtUtc,
        bool pauseEnded
    ) => new(null, null, null, null, null, cursor.NextOccurrenceAtUtc, nextEvaluationAtUtc, status, pauseEnded, false);

    /// <summary>The first occurrence strictly after <paramref name="fromUtc"/>, an anchored interval stepping from its anchor.</summary>
    private static DateTime? NextAfter(ScheduleTiming timing, DateTime fromUtc) =>
        NextOccurrenceCalculator.FirstAfter(
            timing.Expression,
            timing.TimeZoneId,
            timing.ExpressionKind,
            timing.IntervalAnchorAtUtc ?? fromUtc,
            fromUtc
        );

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
