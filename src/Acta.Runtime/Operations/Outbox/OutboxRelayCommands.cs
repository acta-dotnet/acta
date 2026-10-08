namespace Acta.Runtime;

/// <summary>
/// A bounded claim request: expired-lease recovery plus a due-Pending claim of at most
/// <paramref name="BatchSize"/> rows, stamping <paramref name="ClaimToken"/> valid for
/// <paramref name="LeaseTtlSeconds"/> from the source-database clock.
/// </summary>
internal sealed record ClaimOutboxCommand(Guid ClaimToken, int BatchSize, int LeaseTtlSeconds);

/// <summary>Token-CAS finalize of a set of claimed rows by delete or release; neither records a count or error.</summary>
internal sealed record FinalizeOutboxCommand(Guid ClaimToken, IReadOnlyList<Guid> OutboxIds);

/// <summary>Token-CAS reschedule of recoverable failures back to Pending with backoff.</summary>
internal sealed record RescheduleOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxReschedule> Rows);

/// <summary>
/// Per-row reschedule outcome: the row's own incremented failure count, the backoff duration in whole
/// seconds the source database adds to its own clock (<c>next_attempt = source_db_now + BackoffSeconds</c>,
/// so eligibility is anchored to the source clock, not the ledger clock), and the bounded error.
/// </summary>
internal readonly record struct OutboxReschedule(Guid OutboxId, int FailureCount, int BackoffSeconds, string LastError);

/// <summary>
/// Token-CAS transition of claimed rows to Quarantined, one <see cref="OutboxQuarantine"/> per row so a
/// coalesced group can partially quarantine: each row records its own consumed failure count and error.
/// </summary>
internal sealed record QuarantineOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxQuarantine> Rows);

/// <summary>Per-row quarantine outcome: the row's own consumed failure count and bounded error.</summary>
internal readonly record struct OutboxQuarantine(Guid OutboxId, int FailureCount, string? LastError);

/// <summary>
/// A keyset page request over the Quarantined rows, ordered by <c>outbox_id</c> - the one portable
/// unique order across the three source encodings. <paramref name="AfterOutboxId"/> is the last id of
/// the previous page, or null for the first page.
/// </summary>
internal sealed record ListQuarantinedOutboxCommand(int PageSize, Guid? AfterOutboxId);

/// <summary>
/// Operator requeue of Quarantined rows: back to Pending, immediately due, failure budget reset,
/// <c>last_error</c> kept as evidence. A null <paramref name="OutboxIds"/> targets every quarantined row.
/// </summary>
internal sealed record RequeueQuarantinedOutboxCommand(IReadOnlyList<Guid>? OutboxIds);

/// <summary>
/// Operator discard of Quarantined rows: the rows are deleted; the returned ids become event evidence in
/// the ledger. A null <paramref name="OutboxIds"/> targets every quarantined row.
/// </summary>
internal sealed record DiscardQuarantinedOutboxCommand(IReadOnlyList<Guid>? OutboxIds);
