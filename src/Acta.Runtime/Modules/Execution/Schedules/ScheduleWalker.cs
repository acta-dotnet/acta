namespace Acta.Runtime.Modules.Execution.Schedules;

/// <summary>
/// Result of planning one recurring fire: the due schedule names (ordered) the handler sees, the
/// cursor advances to apply on completion, and the post-advance slot MIN (null when the slot is
/// exhausted).
/// </summary>
internal sealed record RecurringFireOutcome(
    IReadOnlyList<string> TriggeringScheduleNames,
    IReadOnlyList<ScheduleAdvance> Advances,
    DateTime? SlotMinNextRunAtUtc
)
{
    /// <summary>
    /// The fire only ends timed pauses that leave nothing due: cursors move and the pauses clear, but no
    /// occurrence runs, so the handler is not called. A fire with no advances at all is a trigger-now.
    /// </summary>
    public bool NothingDue => TriggeringScheduleNames.Count == 0 && Advances.Count > 0;
}

/// <summary>
/// Pure schedule planning over a slot's live schedules. Identifies the due set, advances each due
/// schedule's cursor, and computes the slot MIN, plus a shared reconcile path used by startup
/// upsert, resume, and restart. All time math delegates to <see cref="NextOccurrenceCalculator"/>
/// with no ambient clock.
/// </summary>
internal static class ScheduleWalker
{
    /// <summary>
    /// Plans one recurring fire at <paramref name="nowUtc"/>: the due set (ordered by name) the handler
    /// will see, the cursor advance for each due schedule, and the post-advance slot MIN. A timed pause
    /// that has elapsed is a resume at the instant it ended: an occurrence inside the window is a misfire
    /// for the schedule's policy, one at or after its end is due as usual. The schedule fires only when
    /// that leaves an occurrence due by now, and advances either way, which also clears the pause (see
    /// <c>complete_execution</c>); a timed pause still ahead contributes only its <c>PausedUntilUtc</c> as
    /// the slot's wake point.
    /// </summary>
    public static RecurringFireOutcome PlanFire(IReadOnlyList<LiveSchedule> live, DateTime nowUtc)
    {
        var due = new List<LiveSchedule>();
        var advances = new List<ScheduleAdvance>();
        foreach (var s in live)
        {
            var cursor =
                s.Status == ScheduleStatusCode.Paused
                    ? s.PausedUntilUtc is { } until && until <= nowUtc
                        ? NextOccurrenceCalculator.Reconcile(
                            s.Expression,
                            s.TimeZoneId,
                            s.ExpressionKind,
                            s.MisfireStrategy,
                            s.NextRunAtUtc,
                            until.AddTicks(-1)
                        )
                        : null
                    : s.NextRunAtUtc;
            if (cursor is { } c && c <= nowUtc)
            {
                due.Add(s);
                advances.Add(
                    new ScheduleAdvance(
                        s.Id,
                        NextOccurrenceCalculator.FirstAfter(s.Expression, s.TimeZoneId, s.ExpressionKind, c, nowUtc),
                        s.Version
                    )
                );
            }
            else if (s.Status == ScheduleStatusCode.Paused && s.PausedUntilUtc <= nowUtc)
            {
                advances.Add(new ScheduleAdvance(s.Id, cursor, s.Version));
            }
        }
        due.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        var advancedById = advances.ToDictionary(a => a.ScheduleId, a => a.NextRunAtUtc);

        // An advanced schedule contributes its new cursor; everything else contributes per the pause rule.
        var slotMin = SlotMin(
            live.Select(s =>
                advancedById.TryGetValue(s.Id, out var adv) ? adv
                : s.Status == ScheduleStatusCode.Paused ? s.PausedUntilUtc
                : s.NextRunAtUtc
            )
        );

        return new RecurringFireOutcome(due.Select(s => s.Name).ToList(), advances, slotMin);
    }

    /// <summary>
    /// Recomputes the slot MIN over live schedules using each schedule's misfire policy and stored
    /// cursor at <paramref name="nowUtc"/>, for recurring-aware resume or restart. Active schedules
    /// reconcile via the misfire policy; a timed pause contributes its <c>PausedUntilUtc</c>; an
    /// indefinite pause contributes nothing. Null when no live schedule yields an upcoming occurrence.
    /// </summary>
    public static DateTime? RecomputeSlotMin(IReadOnlyList<LiveSchedule> live, DateTime nowUtc) =>
        SlotMin(
            live.Select(s =>
                s.Status == ScheduleStatusCode.Paused
                    ? s.PausedUntilUtc
                    : NextOccurrenceCalculator.Reconcile(
                        s.Expression,
                        s.TimeZoneId,
                        s.ExpressionKind,
                        s.MisfireStrategy,
                        s.NextRunAtUtc,
                        nowUtc
                    )
            )
        );

    /// <summary>
    /// Reconciles a definition's declared schedules against persisted state at <paramref name="nowUtc"/>.
    /// New schedules seed from the first occurrence after now; active schedules recompute from their
    /// stored cursor using the misfire policy, under an operator's override where one is in force (the
    /// written row still carries the declared values); paused schedules keep their stored cursor untouched and
    /// contribute only a timed <c>PausedUntilUtc</c> to the slot MIN, so operator pause survives a
    /// redeploy. A cursor already due that the running fleet owns stays due for the run that takes it:
    /// its slot has an attempt in flight or is parked part-way through it (<see cref="IsParked"/>), or it
    /// came due less than <see cref="MisfireThreshold"/> ago and is about to be claimed. A parked slot
    /// keeps its own status and wake instant too. Returns the per-schedule reconciled state and the slot's
    /// status and next run, Paused with none when no schedule yields an upcoming occurrence.
    /// </summary>
    public static (IReadOnlyList<SlotSchedule> Schedules, JobStatusCode SlotStatus, DateTime? SlotNextRunAtUtc) Reconcile(
        IReadOnlyList<ScheduleDescriptor> declared,
        IReadOnlyDictionary<string, StoredScheduleState> storedByName,
        DateTime nowUtc
    )
    {
        var schedules = new List<SlotSchedule>(declared.Count);
        var contributions = new List<DateTime?>(declared.Count);
        var slot = storedByName.Values.FirstOrDefault();
        var inFlight = slot?.SlotStatus is JobStatusCode.Dispatched or JobStatusCode.Executing;
        var parked = IsParked(slot, storedByName.Values, nowUtc);

        foreach (var d in declared)
        {
            var timeZone = string.IsNullOrWhiteSpace(d.TimeZoneId) ? "UTC" : d.TimeZoneId;
            var stored = storedByName.TryGetValue(d.ScheduleName, out var s) ? s : null;

            DateTime? cursor;
            DateTime? contribution;
            if (stored is { Status: ScheduleStatusCode.Paused })
            {
                cursor = stored.NextRunAtUtc; // preserve the remembered due point; do not advance a paused schedule
                contribution = stored.PausedUntilUtc; // timed pause is the wake point; indefinite contributes nothing
            }
            else
            {
                // An operator's override is the expression in force. One set for another kind is left
                // behind by a deploy that switched interval and cron, and registration clears it.
                var expression = stored?.ExpressionKind == d.ExpressionKind ? stored.ExpressionOverride ?? d.Expression : d.Expression;
                var zone = string.IsNullOrWhiteSpace(stored?.TimeZoneIdOverride) ? timeZone : stored.TimeZoneIdOverride;
                var due = stored?.NextRunAtUtc;
                // Rewriting the schedule of an attempt in flight bumps the row's version, which refuses that
                // attempt's own advance, so the cursor it was armed for moves past it here instead.
                cursor =
                    due <= nowUtc && (inFlight || parked || due > nowUtc - MisfireThreshold)
                        ? inFlight && due <= slot!.SlotNextRunAtUtc && Redeclares(stored!, d, timeZone)
                            ? NextOccurrenceCalculator.FirstAfter(expression, zone, d.ExpressionKind, due.Value, nowUtc)
                            : due
                        : NextOccurrenceCalculator.Reconcile(expression, zone, d.ExpressionKind, d.MisfireStrategy, due, nowUtc);
                contribution = cursor;
            }

            schedules.Add(
                new SlotSchedule(d.ScheduleName, d.Expression, timeZone, d.MisfireStrategy, d.ExpressionKind, d.Description, cursor)
            );
            contributions.Add(contribution);
        }

        if (parked)
        {
            return (schedules, slot!.SlotStatus!.Value, slot.SlotNextRunAtUtc);
        }

        // No schedule offering a run, a dropped declaration included, is Paused with no next run, which a
        // returning declaration lifts; Cancelled is only ever an operator's or handler's outcome.
        var slotMin = SlotMin(contributions);
        return (schedules, slotMin is null ? JobStatusCode.Paused : JobStatusCode.Ready, slotMin);
    }

    /// <summary>
    /// How far behind now a stored occurrence may be at a worker start and still count as due rather than
    /// missed: a running fleet claims a due slot within its claim latency, so a start in that moment must
    /// not take the occurrence for a misfire. One further behind was missed, to downtime.
    /// </summary>
    internal static readonly TimeSpan MisfireThreshold = TimeSpan.FromMinutes(1);

    /// <summary>Whether registering <paramref name="d"/> rewrites the stored row: a declared column differs.</summary>
    private static bool Redeclares(StoredScheduleState stored, ScheduleDescriptor d, string timeZone) =>
        stored.Expression != d.Expression
        || stored.TimeZoneId != timeZone
        || stored.ExpressionKind != d.ExpressionKind
        || stored.MisfireStrategy != d.MisfireStrategy
        || stored.Description != d.Description;

    /// <summary>
    /// Whether a recurring slot is parked part-way through an occurrence: Suspended on a wait, or Ready at a
    /// wake instant (a sleep, a step retry, a rate or concurrency bounce, a reclaim) past an active
    /// schedule's cursor that is already due. A re-arm never advances cursors, so that gap is the occurrence
    /// waiting to resume.
    /// </summary>
    private static bool IsParked(StoredScheduleState? slot, IEnumerable<StoredScheduleState> rows, DateTime nowUtc) =>
        slot?.SlotStatus == JobStatusCode.Suspended
        || slot is { SlotStatus: JobStatusCode.Ready, SlotNextRunAtUtc: { } wake }
            && rows.Any(s => s.Status == ScheduleStatusCode.Active && s.NextRunAtUtc is { } due && due <= nowUtc && due < wake);

    /// <summary>
    /// Whether a job is held: Paused with a next run. An operator's pause and a handler's own pause always
    /// leave a next run, while a recurring job whose schedules offer none is Paused with no next run. The
    /// schedule verbs, startup registration, and trigger-now test this inside their own statements and never
    /// move a held job; only a job resume, restart, or cancel lifts the hold.
    /// </summary>
    public static bool IsHeld(JobStatusCode? status, DateTime? nextRunAtUtc) => status == JobStatusCode.Paused && nextRunAtUtc is not null;

    /// <summary>
    /// The slot's next run is the earliest schedule contribution: active schedules offer their
    /// cursor, a timed pause still ahead offers its wake instant, an indefinite pause or exhausted
    /// schedule offers nothing (null, ignored by Min).
    /// </summary>
    private static DateTime? SlotMin(IEnumerable<DateTime?> contributions) => contributions.Min();
}
