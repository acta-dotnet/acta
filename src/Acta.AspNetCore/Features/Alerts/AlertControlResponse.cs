namespace Acta.AspNetCore.Features.Alerts;

/// <summary>
/// HTTP projection of an <see cref="AlertControlResult"/>. Returned for both applied (200) and
/// not-found (404) outcomes; there is no rejected (409) case since re-acknowledging or re-resolving is
/// always Applied (idempotent, no mutation).
/// </summary>
internal sealed record AlertControlResponse(long AlertId, JobControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);
