namespace Acta;

/// <summary>What parking a requeue of a source's quarantined rows did.</summary>
public sealed record RequeueOutboxResponse(ControlAction Action, DateTime? PendingSinceUtc) : IOutboxControlResponse;
