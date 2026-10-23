namespace Acta;

/// <summary>The id of one Quarantined row the command targets.</summary>
[DbProjection]
internal sealed record MatchQuarantinedOutboxRow(Guid OutboxId);
