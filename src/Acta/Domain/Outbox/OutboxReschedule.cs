namespace Acta;

/// <summary>
/// Per-row reschedule outcome: the row's own incremented failure count, the backoff duration in whole
/// seconds the source database adds to its own clock (<c>next_attempt = source_db_now + BackoffSeconds</c>,
/// so eligibility is anchored to the source clock, not the ledger clock), and the bounded error.
/// </summary>
internal readonly record struct OutboxReschedule(Guid OutboxId, int FailureCount, int BackoffSeconds, string LastError);
