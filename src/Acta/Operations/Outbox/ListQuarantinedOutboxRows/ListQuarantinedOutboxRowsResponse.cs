namespace Acta;

/// <summary>The Quarantined rows of one keyset read, ordered by outbox id.</summary>
internal readonly record struct ListQuarantinedOutboxRowsResponse(IReadOnlyList<ListQuarantinedOutboxItem> Items);
