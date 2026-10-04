namespace Acta;

/// <summary>What parking a discard of a source's quarantined rows did.</summary>
public sealed record DiscardOutboxResponse(ControlAction Action, DateTime? PendingSinceUtc) : IOutboxControlResponse;
