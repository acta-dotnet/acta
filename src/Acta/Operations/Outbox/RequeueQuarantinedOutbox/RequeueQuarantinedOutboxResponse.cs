namespace Acta;

/// <summary>The ids of the rows the requeue moved back to Pending.</summary>
internal readonly record struct RequeueQuarantinedOutboxResponse(IReadOnlyList<Guid> OutboxIds);
