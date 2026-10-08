namespace Acta.Runtime;

/// <summary>The id of one row the requeue moved back to Pending, returned by the same statement.</summary>
[DbProjection]
internal sealed record RequeueQuarantinedOutboxRow(Guid OutboxId);
