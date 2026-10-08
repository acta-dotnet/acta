namespace Acta.Runtime;

/// <summary>The Quarantined rows of one keyset read, ordered by outbox id.</summary>
internal readonly record struct ListQuarantinedOutboxResponse(IReadOnlyList<ListOutboxQuarantinedItem> Items);
