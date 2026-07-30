namespace Acta.Features.Execution;

/// <summary>
/// Outcome of a single <c>JobExecutor.RunOnceAsync</c> tick.
/// </summary>
internal enum RunOnceOutcome : byte
{
    /// <summary>
    /// Nothing executed this tick: either no Ready job was claimable, or a claimed job's
    /// <c>start_execution</c> CAS missed (the row was reclaimed, reassigned, or moved out of
    /// Dispatched by an operator control verb between claim and start). Either way the worker
    /// mutated nothing and backs off.
    /// </summary>
    NothingClaimed = 1,

    /// <summary>Claimed and ran a job; handler completed; row terminal-<c>Done</c>.</summary>
    Completed = 2,

    /// <summary>Claimed and ran a job; handler threw; row terminal-<c>Failed</c>.</summary>
    Failed = 3,

    /// <summary>
    /// Claimed and ran a job; the handler re-armed it for a subsequent claim via reschedule or durable
    /// sleep. The Job is back at <c>Ready</c> with a forward-dated <c>NextRunAtUtc</c>; budget-neutral.
    /// Treated as progress (no backoff), like <see cref="Completed"/>.
    /// </summary>
    Rearmed = 4,
}
