namespace Acta.Runtime;

/// <summary>
/// A computed cursor advance for one schedule, applied by <c>complete_execution</c> on a recurring fire.
/// A null <c>NextRunAtUtc</c> clears the schedule's cursor (no further occurrences).
/// <c>ExpectedVersion</c> is the schedule's version the plan was made against: the advance is applied
/// only if the row still carries it, so an operator edit or a re-registration that landed while the
/// attempt ran keeps its cursor instead of being overwritten by a plan made before it. Null applies
/// the advance unguarded, which is what a build predating the guard sends.
/// </summary>
internal sealed record ScheduleAdvance(long ScheduleId, DateTime? NextRunAtUtc, int? ExpectedVersion = null);

internal enum CompleteExecutionAction : byte
{
    Completed = 1,
    NotOwner = 2,
    AlreadyTerminal = 3,
}

/// <summary>
/// Completion outcome plus the job's resulting runtime state, read in the same round trip so the
/// caller can react to where the row landed. Publishes a wakeup when the final status is <c>Ready</c>
/// (immediate retry, recurring roll-over, or a signal that arrived <c>Set</c> while the handler ran),
/// the one Ready transition no other publish site can see.
/// </summary>
/// <param name="Action">Whether this call completed the attempt, lost ownership (NotOwner), or found the job already terminal.</param>
/// <param name="FinalStatusCode">The job's <c>status_code</c> after this call: on <c>Completed</c> the status this completion produced; on NotOwner/AlreadyTerminal the row's current status (null when the row no longer exists).</param>
/// <param name="FinalNextRunAtUtc">The job's <c>next_run_at_utc</c> after this call, on the same produced-vs-current basis as <paramref name="FinalStatusCode"/>.</param>
/// <param name="DbNowUtc">The routine's clock reading, for due-now comparison against <paramref name="FinalNextRunAtUtc"/> with no host-clock assumption.</param>
/// <param name="ParentReleased">True when this terminal landing's child-done raise flipped a Suspended parent to Ready; the caller wakes all worker namespaces (the parent may live in another namespace).</param>
/// <param name="LanePromoted">True when this terminal landing handed its lane to the next member, which is now Ready; the caller wakes its namespace.</param>
[DbProjection]
internal sealed record CompleteExecutionRow(
    CompleteExecutionAction Action,
    byte? FinalStatusCode,
    DateTime? FinalNextRunAtUtc,
    DateTime DbNowUtc,
    bool ParentReleased,
    bool LanePromoted = false
);
