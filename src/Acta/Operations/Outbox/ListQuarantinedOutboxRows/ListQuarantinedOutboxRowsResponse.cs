namespace Acta;

/// <summary>The Quarantined rows of one keyset read, ordered by job ref.</summary>
internal readonly record struct ListQuarantinedOutboxRowsResponse(IReadOnlyList<ListQuarantinedOutboxItem> Items);
