namespace Acta;

/// <summary>
/// The one evaluator every scheduling path shares: given a schedule's source, policy, cursor, and the capacity the
/// admitter read, decides at the instant it is given what to admit, what to skip or expire, where the cursor moves,
/// and when to look again. Pure: no clock, no database, no provider call of its own. Occurrences are nominal instants;
/// a missed one is decided by the misfire policy and never shifted.
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
        ScheduleSource source,
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
            // A calendar is code or catalog content that can change under a stored cursor: an occurrence the source no
            // longer produces is skipped as such, and the cursor moves to the first it does, decided on the next pass.
            if (source.Calendar is not null || source.Availability is not null)
            {
                var still = NextOccurrenceCalculator.Next(source, next.AddTicks(-1));
                if (still != next)
                {
                    return Moved(still, pauseEnded, pausedByExpiry: false, skippedFrom: next, skippedThrough: next, sourceChanged: true);
                }
            }

            latest = LatestDue(source, next, now);

            // Expiration comes before the misfire policy: every due occurrence that aged past the expiry is decided
            // without work, and under Pause the schedule then stops for an operator. An operator's resume and a timed
            // pause's end expire the stale backlog once without pausing again, so the schedule can move on.
            if (policy.ExpireAfter is { } age && next <= now - age)
            {
                expiredFrom = next;
                expiredThrough = LatestDue(source, next, now - age);
                pausedByExpiry =
                    policy.ExpireAction == ScheduleExpireActionCode.Pause
                    && !pauseEnded
                    && cursor.EvaluationStatus != EvaluationStatusCode.Resumed;
                var remaining = NextOccurrenceCalculator.Next(source, expiredThrough.Value);
                if (remaining is null || remaining > now)
                {
                    return Moved(remaining, pauseEnded, pausedByExpiry, expiredFrom: expiredFrom, expiredThrough: expiredThrough);
                }

                next = remaining.Value;
            }

            following = NextOccurrenceCalculator.FirstAfter(source, source.Timing.IntervalAnchorAtUtc ?? latest, now);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException)
        {
            return Unchanged(cursor, EvaluationStatusCode.EvaluationError, now + ErrorRetryDelay, pauseEnded) with { Error = ex.Message };
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
                    skippedThrough = PreviousDue(source, next, latest);
                }
                break;
            case MisfireStrategyCode.CatchUpAll:
                // The earliest due occurrence fires alone; the rest of the backlog is due again at once, so the next pass
                // comes straight back and admits the next one as capacity allows.
                admit = new AdmittedOccurrence(next, next);
                following = NextOccurrenceCalculator.Next(source, next);
                break;
            case MisfireStrategyCode.Skip:
                admit = onTime ? new AdmittedOccurrence(latest, latest) : null;
                if (next < latest || !onTime)
                {
                    skippedFrom = next;
                    skippedThrough = onTime ? PreviousDue(source, next, latest) : latest;
                }
                break;
            default:
                throw new NotSupportedException($"Misfire strategy {policy.Misfire} is not supported by this build.");
        }

        return Moved(following, pauseEnded, pausedByExpiry, admit, skippedFrom, skippedThrough, expiredFrom, expiredThrough);
    }

    private static ScheduleEvaluation Unchanged(
        ScheduleCursor cursor,
        EvaluationStatusCode status,
        DateTime? nextEvaluationAtUtc,
        bool pauseEnded
    ) => new(null, null, null, null, null, cursor.NextOccurrenceAtUtc, nextEvaluationAtUtc, status, pauseEnded, false);

    /// <summary>
    /// A cursor moved to <paramref name="next"/>: Exhausted when nothing follows, evaluated again at the occurrence
    /// unless an expiry pause holds the schedule for an operator.
    /// </summary>
    private static ScheduleEvaluation Moved(
        DateTime? next,
        bool pauseEnded,
        bool pausedByExpiry,
        AdmittedOccurrence? admit = null,
        DateTime? skippedFrom = null,
        DateTime? skippedThrough = null,
        DateTime? expiredFrom = null,
        DateTime? expiredThrough = null,
        bool sourceChanged = false
    ) =>
        new(
            admit,
            skippedFrom,
            skippedThrough,
            expiredFrom,
            expiredThrough,
            next,
            pausedByExpiry ? null : next,
            next is null ? EvaluationStatusCode.Exhausted : EvaluationStatusCode.Scheduled,
            pauseEnded,
            pausedByExpiry,
            SourceChanged: sourceChanged
        );

    /// <summary>
    /// The last occurrence at or before <paramref name="nowUtc"/>, starting from the due <paramref name="fromUtc"/>: an
    /// interval's grid arithmetic, or a bounded walk when the source is a cron, a calendar, or filtered by availability.
    /// </summary>
    internal static DateTime LatestDue(ScheduleSource source, DateTime fromUtc, DateTime nowUtc)
    {
        var timing = source.Timing;
        if (timing.ExpressionKind == ScheduleExpressionKindCode.Interval && source.Availability is null)
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
            if (NextOccurrenceCalculator.Next(source, current) is not { } candidate || candidate > nowUtc)
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
    private static DateTime PreviousDue(ScheduleSource source, DateTime fromUtc, DateTime latestUtc)
    {
        if (source.Timing.ExpressionKind == ScheduleExpressionKindCode.Interval && source.Availability is null)
        {
            return latestUtc - NextOccurrenceCalculator.ParseInterval(source.Timing.Expression);
        }

        var current = fromUtc;
        while (NextOccurrenceCalculator.Next(source, current) is { } candidate && candidate < latestUtc)
        {
            current = candidate;
        }

        return current;
    }
}
