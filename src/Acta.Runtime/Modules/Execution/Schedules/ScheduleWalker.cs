using Acta.Modules.Execution;

namespace Acta.Modules.Execution.Schedules;

/// <summary>
/// Result of planning one recurring fire: the due schedule names (ordered) the handler sees, the
/// cursor advances to apply on completion, and the post-advance slot MIN (null when the slot is
/// exhausted).
/// </summary>
internal sealed record RecurringFireOutcome(
    IReadOnlyList<string> TriggeringScheduleNames,
    IReadOnlyList<ScheduleAdvance> Advances,
    DateTime? SlotMinNextRunAtUtc
);

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
    /// will see, the cursor advance for each due schedule, and the post-advance slot MIN. A timed-paused
    /// schedule whose <c>PausedUntilUtc</c> has elapsed counts as due and advances like an active one
    /// (the advance also clears the pause; see <c>complete_execution</c>); a timed pause still ahead
    /// contributes only its <c>PausedUntilUtc</c> as the slot's wake point.
    /// </summary>
    public static RecurringFireOutcome PlanFire(IReadOnlyList<LiveSchedule> live, DateTime nowUtc)
    {
        var due = live.Where(s => IsDue(s, nowUtc)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        var advances = due.Select(s => new ScheduleAdvance(
                s.Id,
                NextOccurrenceCalculator.FirstAfter(
                    s.Expression,
                    s.TimeZone,
                    s.ExpressionKind,
                    (s.NextRunAtUtc ?? s.PausedUntilUtc)!.Value,
                    nowUtc
                )
            ))
            .ToList();

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
                    : NextOccurrenceCalculator.Reconcile(s.Expression, s.TimeZone, s.ExpressionKind, s.Misfire, s.NextRunAtUtc, nowUtc)
            )
        );

    /// <summary>
    /// Reconciles a definition's declared schedules against persisted state at <paramref name="nowUtc"/>.
    /// New schedules seed from the first occurrence after now; active schedules recompute from their
    /// stored cursor using the misfire policy; paused schedules keep their stored cursor untouched and
    /// contribute only a timed <c>PausedUntilUtc</c> to the slot MIN, so operator pause survives a
    /// redeploy. Returns the per-schedule reconciled state and the slot MIN (null when no schedule yields
    /// an upcoming occurrence).
    /// </summary>
    public static (IReadOnlyList<SlotSchedule> Schedules, DateTime? SlotMin) Reconcile(
        IReadOnlyList<JobScheduleDescriptor> declared,
        IReadOnlyDictionary<string, StoredScheduleState> storedByName,
        DateTime nowUtc
    )
    {
        var schedules = new List<SlotSchedule>(declared.Count);
        var contributions = new List<DateTime?>(declared.Count);

        foreach (var d in declared)
        {
            var timeZone = string.IsNullOrWhiteSpace(d.TimeZone) ? "UTC" : d.TimeZone;
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
                cursor = NextOccurrenceCalculator.Reconcile(
                    d.Expression,
                    timeZone,
                    d.ExpressionKind,
                    d.Misfire,
                    stored?.NextRunAtUtc,
                    nowUtc
                );
                contribution = cursor;
            }

            schedules.Add(new SlotSchedule(d.ScheduleName, d.Expression, timeZone, d.Misfire, d.ExpressionKind, d.Description, cursor));
            contributions.Add(contribution);
        }

        return (schedules, SlotMin(contributions));
    }

    // A schedule fires when active and due, or when a timed pause has elapsed (auto-resume).
    private static bool IsDue(LiveSchedule s, DateTime nowUtc) =>
        s.Status == ScheduleStatusCode.Paused ? s.PausedUntilUtc is { } until && until <= nowUtc : s.NextRunAtUtc is { } n && n <= nowUtc;

    // The slot's next run is the earliest schedule contribution: active schedules offer their cursor, a
    // timed pause still ahead offers its wake instant, an indefinite pause or exhausted schedule offers
    // nothing (null, ignored by Min).
    private static DateTime? SlotMin(IEnumerable<DateTime?> contributions) => contributions.Min();
}
