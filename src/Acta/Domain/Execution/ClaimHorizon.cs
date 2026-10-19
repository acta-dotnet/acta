namespace Acta;

/// <summary>
/// The empty-claim horizon: the routine's clock reading and the earliest claimable row's effective run time in the
/// namespace, <c>MIN(COALESCE(next_run_at_utc, db_now))</c> over every Ready row and every Suspended row carrying a
/// durable wait's expiration, due-now rows included (a concurrency-key bounce re-arms Ready with a forward-dated
/// <c>next_run_at_utc</c>, so it appears at its due instant). <c>NextReadyAtUtc</c> is null only when no such row
/// exists; a value at or before <c>DbNowUtc</c> means due rows were transiently locked away (SKIP LOCKED), so the
/// caller retries after a short floor rather than sleeping out the safety interval. Both instants are DB-sourced.
/// </summary>
internal readonly record struct ClaimHorizon(DateTime DbNowUtc, DateTime? NextReadyAtUtc);
