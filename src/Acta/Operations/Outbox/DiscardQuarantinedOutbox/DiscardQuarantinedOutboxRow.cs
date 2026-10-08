namespace Acta;

/// <summary>The id of one row the discard deleted, returned by the same statement.</summary>
[DbProjection]
internal sealed record DiscardQuarantinedOutboxRow(Guid OutboxId);
