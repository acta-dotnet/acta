namespace Acta;

/// <summary>
/// Operator discard of Quarantined rows: the rows are deleted; the returned ids become event evidence in
/// the ledger. A null <paramref name="OutboxIds"/> targets every quarantined row.
/// </summary>
internal sealed record DiscardQuarantinedOutboxCommand(IReadOnlyList<Guid>? OutboxIds);
