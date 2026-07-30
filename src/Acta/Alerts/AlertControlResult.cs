namespace Acta;

/// <summary>
/// Outcome of an <see cref="IAlerts"/> control verb (Acknowledge / Resolve). Re-acknowledging an
/// already-acknowledged alert (or re-resolving an already-resolved one) is
/// <see cref="JobControlAction.Applied"/> without mutation: the existing timestamp is returned
/// unchanged and no second event is emitted. Never <see cref="JobControlAction.Rejected"/>: there is
/// no transition-legality guard beyond the alert existing.
/// </summary>
/// <param name="AlertId">The targeted alert's id.</param>
/// <param name="Action">Whether the control verb was applied or the alert was absent.</param>
/// <param name="AcknowledgedAtUtc">When the alert was acknowledged; null until acknowledged.</param>
/// <param name="ResolvedAtUtc">When the alert was resolved; null until resolved.</param>
public sealed record AlertControlResult(long AlertId, JobControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);
