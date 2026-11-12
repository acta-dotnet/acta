namespace Acta;

/// <summary>
/// The one evaluator every scheduling path shares: given a schedule's source, policy, cursor, and the capacity the
/// admitter read, decides at the instant it is given what to admit, what to skip or expire, where the cursor moves,
/// and when to look again. Pure: no clock, no database, no provider call of its own. Occurrences are nominal instants;
/// a missed one is decided by the misfire policy and never shifted.
/// </summary>
internal static class ScheduleEvaluator
{
    /// <summary>
    /// How long past its instant a due occurrence still counts as on time under <c>Skip</c> rather than missed. It
    /// covers a slow admission pass, not downtime.
    /// </summary>
    internal static readonly TimeSpan MisfireGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an evaluation error waits before the same cursor is evaluated again. Never shorter than the grace: an
    /// error at the pass that ends a pause keeps the cursor and forgets the pause, and the occurrence it kept must be
    /// past its grace when the retry decides it, or it would fire from inside the pause.
    /// </summary>
    internal static readonly TimeSpan ErrorRetryDelay = MisfireGrace;

    /// <summary>
    /// The due occurrences one evaluation steps through before it searches the rest of the backlog instead. Stepping
    /// is exact for any source, the search for any whose answers do not depend on where a seek starts, and a backlog
    /// deeper than this is decided either way: none is an error.
    /// </summary>
    internal const int DueSteps = 1000;

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
                return Unchanged(cursor, ScheduleEvaluationStatusCode.Scheduled, null, pauseEnded: false);
            }

            if (until > now)
            {
                return Unchanged(cursor, ScheduleEvaluationStatusCode.Scheduled, until, pauseEnded: false);
            }

            pauseEnded = true;
            downtimeEndUtc = until;
        }
        else if (cursor.Status != ScheduleStatusCode.Active)
        {
            return Unchanged(cursor, ScheduleEvaluationStatusCode.Scheduled, null, pauseEnded: false);
        }

        if (cursor.NextOccurrenceAtUtc is not { } next)
        {
            return Unchanged(cursor, ScheduleEvaluationStatusCode.Exhausted, null, pauseEnded);
        }

        next = DateTime.SpecifyKind(next, DateTimeKind.Utc);
        if (next > now)
        {
            return Unchanged(cursor, ScheduleEvaluationStatusCode.Scheduled, next, pauseEnded);
        }

        DateTime? expiredFrom = null;
        DateTime? expiredThrough = null;
        var pausedByExpiry = false;
        AdmittedOccurrence? admit;
        DateTime? following;
        DateTime? skippedFrom = null;
        DateTime? skippedThrough = null;
        try
        {
            // A schedule in error stays in error while its source still throws, whatever the slot can take: parked, it
            // would read as waiting, and the settlement that woke it would start the same failure over.
            if (cursor.EvaluationStatus == ScheduleEvaluationStatusCode.EvaluationError)
            {
                _ = NextOccurrenceCalculator.Next(source, next);
            }

            // A calendar is code or catalog content that can change under a stored cursor, and a cursor can sit outside
            // the bounds, as one a test host plants: an occurrence the source no longer produces is skipped as such, and
            // the cursor moves to the first it does, decided on the next pass.
            if (
                source.Calendar is not null
                || source.Availability is not null
                || source.Timing.StartAtUtc is not null
                || source.Timing.EndBeforeUtc is not null
            )
            {
                // An interval with no anchor grids from its cursor: gridded from the tick before, it never answers the cursor.
                var still = NextOccurrenceCalculator.FirstAfter(source, source.Timing.IntervalAnchorAtUtc ?? next, next.AddTicks(-1));
                if (still != next)
                {
                    // A cursor moved into a timed pause's window keeps the pause, so the pass that decides it has the window.
                    var ended = pauseEnded && !(still < downtimeEndUtc);
                    return Moved(still, ended, pausedByExpiry: false, skippedFrom: next, skippedThrough: next, sourceChanged: true);
                }
            }

            // Expiration comes before capacity and the misfire policy: every due occurrence that aged past the expiry
            // is decided without work, whatever the slot or the schedule can take, and under Pause the schedule then
            // stops for an operator. An operator's resume and a timed pause's end expire the stale backlog once without
            // pausing again, so the schedule can move on.
            if (policy.ExpireAfter is { } age && next <= now - age)
            {
                expiredFrom = next;
                expiredThrough = LatestDue(source, next, now - age).Latest;
                pausedByExpiry =
                    policy.ExpireAction == ScheduleExpireActionCode.Pause
                    && !pauseEnded
                    && cursor.EvaluationStatus != ScheduleEvaluationStatusCode.Resumed;
                var remaining = NextOccurrenceCalculator.Next(source, expiredThrough.Value);
                if (remaining is null || remaining > now)
                {
                    return Moved(remaining, pauseEnded, pausedByExpiry, expiredFrom: expiredFrom, expiredThrough: expiredThrough);
                }

                next = remaining.Value;
            }

            // Under Skip a timed pause whose window holds everything due skips it at the pass that ends it, whatever the
            // slot can take: a schedule parked below is evaluated again as Active, with the pause gone and only the grace
            // to judge by. With an occurrence since the pause due as well, that one is the latest and Skip fires no other.
            if (pauseEnded && policy.Misfire == MisfireStrategyCode.Skip && next < downtimeEndUtc)
            {
                var through = LatestDue(source, next, downtimeEndUtc.AddTicks(-1)).Latest;
                var resumed = NextOccurrenceCalculator.Next(source, through);
                if (resumed is null || resumed > now)
                {
                    return Moved(resumed, pauseEnded, pausedByExpiry, null, next, through, expiredFrom, expiredThrough);
                }
            }

            // Parked, the schedule keeps what the expiry decided and waits for the settlement or the operator that
            // frees the slot; paused by the expiry, it waits for an operator instead. An expiry that pauses the schedule
            // also wakes it when the waiting occurrence ages past the expiry, which is decided whatever the slot can
            // take; one that only discards is decided as a range when the slot frees.
            if (capacity != AdmissionCapacity.Available)
            {
                var parked =
                    capacity == AdmissionCapacity.Full
                        ? ScheduleEvaluationStatusCode.WaitingForCapacity
                        : ScheduleEvaluationStatusCode.JobUnavailable;
                return new ScheduleEvaluation(
                    null,
                    null,
                    null,
                    expiredFrom,
                    expiredThrough,
                    next,
                    !pausedByExpiry && policy.ExpireAction == ScheduleExpireActionCode.Pause ? next + policy.ExpireAfter : null,
                    pausedByExpiry ? ScheduleEvaluationStatusCode.Scheduled : parked,
                    pauseEnded,
                    pausedByExpiry
                );
            }

            // The last due occurrence and the one before it, asked only for a policy that coalesces into it: CatchUpAll
            // admits the cursor alone, so a deep backlog costs it nothing per pass.
            (DateTime Latest, DateTime? Before)? latestDue = null;
            (DateTime Latest, DateTime? Before) Due() => latestDue ??= LatestDue(source, next, now);
            DateTime Latest() => Due().Latest;

            switch (policy.Misfire)
            {
                case MisfireStrategyCode.CatchUpOnce:
                    // One firing stands for every due occurrence: the earliest is the nominal one, the latest the boundary.
                    admit = new AdmittedOccurrence(next, Latest());
                    break;
                case MisfireStrategyCode.CatchUpLatest:
                    // The latest due occurrence fires however late it is; the earlier ones are coalesced into it as a skip.
                    admit = new AdmittedOccurrence(Latest(), Latest());
                    if (next < Latest())
                    {
                        skippedFrom = next;
                        skippedThrough = PreviousDue(source, Due());
                    }
                    break;
                case MisfireStrategyCode.CatchUpAll:
                    // The earliest due occurrence fires alone; the rest of the backlog is due again at once, so the next
                    // pass comes straight back and admits the next one as capacity allows.
                    admit = new AdmittedOccurrence(next, next);
                    break;
                case MisfireStrategyCode.Skip:
                    // The latest due occurrence is on time within the grace, which is what keeps a slow pass from
                    // counting as a misfire; every earlier one is missed, as the latest is when it is late.
                    var onTime = Latest() >= now - MisfireGrace;
                    admit = onTime ? new AdmittedOccurrence(Latest(), Latest()) : null;
                    if (next < Latest() || !onTime)
                    {
                        skippedFrom = next;
                        skippedThrough = onTime ? PreviousDue(source, Due()) : Latest();
                    }
                    break;
                default:
                    throw new NotSupportedException($"Misfire strategy {policy.Misfire} is not supported by this build.");
            }

            following =
                policy.Misfire == MisfireStrategyCode.CatchUpAll
                    ? NextOccurrenceCalculator.Next(source, next)
                    : NextOccurrenceCalculator.FirstAfter(source, source.Timing.IntervalAnchorAtUtc ?? Latest(), now);
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            return Unchanged(cursor, ScheduleEvaluationStatusCode.EvaluationError, now + ErrorRetryDelay, pauseEnded) with
            {
                Error = ex.Message,
            };
        }

        return Moved(following, pauseEnded, pausedByExpiry, admit, skippedFrom, skippedThrough, expiredFrom, expiredThrough);
    }

    // What a schedule's own source throws when it cannot be read: an expression, a zone, or a calendar.
    private static bool IsSourceFailure(Exception ex) =>
        ex is FormatException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException;

    /// <summary>
    /// Runs a preview's walk of a source. What the admitter would park as an evaluation error is thrown as
    /// <see cref="ScheduleEvaluationException"/> with the source's own message; a calendar with no published version
    /// keeps its own exception.
    /// </summary>
    internal static T Preview<T>(Func<T> walk)
    {
        try
        {
            return walk();
        }
        catch (Exception ex) when (ex is not CalendarUnavailableException && IsSourceFailure(ex))
        {
            throw new ScheduleEvaluationException(ex.Message, ex);
        }
    }

    private static ScheduleEvaluation Unchanged(
        ScheduleCursor cursor,
        ScheduleEvaluationStatusCode status,
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
            next is null ? ScheduleEvaluationStatusCode.Exhausted : ScheduleEvaluationStatusCode.Scheduled,
            pauseEnded,
            pausedByExpiry,
            SourceChanged: sourceChanged
        );

    /// <summary>
    /// The last occurrence at or before <paramref name="nowUtc"/>, starting from the due <paramref name="fromUtc"/>: an
    /// interval's grid arithmetic, cut at the schedule's end so no coalescing policy admits or covers an instant past it,
    /// or, for any other source, up to <paramref name="dueSteps"/> steps and then a search, so a backlog of any depth
    /// costs a bounded number of seeks. The occurrence just before the last comes with it, the end of a skipped range:
    /// null when the cursor is itself the last, and for a grid interval, whose caller subtracts the interval. Only a
    /// test passes its own <paramref name="dueSteps"/>, to hold the search alone to the steps.
    /// </summary>
    internal static (DateTime Latest, DateTime? Before) LatestDue(
        ScheduleSource source,
        DateTime fromUtc,
        DateTime nowUtc,
        int dueSteps = DueSteps
    )
    {
        var timing = source.Timing;
        if (OnGrid(source))
        {
            var interval = NextOccurrenceCalculator.ParseInterval(timing.Expression);
            var anchor = DateTime.SpecifyKind(timing.IntervalAnchorAtUtc ?? fromUtc, DateTimeKind.Utc);
            var limit = timing.EndBeforeUtc is { } end && end <= nowUtc ? end.AddTicks(-1) : nowUtc;
            // Whole ticks: a double would round a span of decades onto the next grid instant, the end itself included.
            var elapsed = (limit - anchor).Ticks;
            var steps = elapsed >= 0 ? elapsed / interval.Ticks : -((-elapsed + interval.Ticks - 1) / interval.Ticks);
            var latest = anchor.AddTicks(interval.Ticks * steps);
            return (latest < fromUtc ? fromUtc : latest, null);
        }

        // One anchor for every seek: an unanchored interval filtered by availability grids from the instant it is
        // asked, and only a fixed grid makes the occurrences one set that a search can halve.
        var seekAnchor = timing.IntervalAnchorAtUtc ?? fromUtc;
        DateTime? before = null;
        var current = fromUtc;
        for (var stepped = 0; stepped < dueSteps; stepped++)
        {
            if (After(source, seekAnchor, current) is not { } candidate || candidate > nowUtc)
            {
                return (current, before);
            }

            before = current;
            current = candidate;
        }

        var lastDue = LastAtOrBefore(source, seekAnchor, current, nowUtc);
        return lastDue == current ? (current, before) : (lastDue, LastAtOrBefore(source, seekAnchor, current, lastDue.AddTicks(-1)));
    }

    /// <summary>
    /// The last occurrence at or before <paramref name="limitUtc"/>, given one known at <paramref name="knownUtc"/>. The
    /// first seek is the one a step would make, so a search that finds nothing more due fails only where a step would.
    /// After it, a seek from the middle of the span either finds an occurrence in its upper half, which becomes the
    /// known one, or shows that half empty, so the span halves on every seek however many occurrences it holds.
    /// </summary>
    private static DateTime LastAtOrBefore(ScheduleSource source, DateTime anchorUtc, DateTime knownUtc, DateTime limitUtc)
    {
        if (After(source, anchorUtc, knownUtc) is not { } next || next > limitUtc)
        {
            return knownUtc;
        }

        var last = next;
        // No occurrence lies after this instant and at or before the limit.
        var emptyAfter = limitUtc;
        while (last < emptyAfter)
        {
            // Halved in whole ticks, which rounds down: the middle stays below the bound, so a span of one tick shrinks.
            var middle = last.AddTicks((emptyAfter - last).Ticks / 2);
            if (After(source, anchorUtc, middle) is { } found && found <= limitUtc)
            {
                last = found;
            }
            else
            {
                emptyAfter = middle;
            }
        }

        return last;
    }

    /// <summary>
    /// The first occurrence after <paramref name="afterUtc"/>, refused when it does not advance: a cron in a zone that
    /// skipped a calendar day can answer the seek instant or one before it, and a search on such answers never ends.
    /// </summary>
    private static DateTime? After(ScheduleSource source, DateTime anchorUtc, DateTime afterUtc)
    {
        var next = NextOccurrenceCalculator.FirstAfter(source, anchorUtc, afterUtc);
        return next <= afterUtc
            ? throw new InvalidOperationException(
                $"'{source.Timing.Expression}' answered {next:O}, not after {afterUtc:O}; an occurrence must advance."
            )
            : next;
    }

    /// <summary>The occurrence just before a due range's last, the end of a skipped range; asked only of a range of two or more.</summary>
    private static DateTime PreviousDue(ScheduleSource source, (DateTime Latest, DateTime? Before) due) =>
        OnGrid(source) ? due.Latest - NextOccurrenceCalculator.ParseInterval(source.Timing.Expression) : due.Before!.Value;

    /// <summary>An interval with no availability filter: its occurrences are grid arithmetic, never a walk.</summary>
    private static bool OnGrid(ScheduleSource source) =>
        source.Timing.ExpressionKind == ScheduleExpressionKindCode.Interval && source.Availability is null;
}
