namespace Acta;

/// <summary>What acknowledging an alert did, and its timestamps after the attempt.</summary>
public sealed record AcknowledgeAlertResponse(AlertRef AlertRef, ControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc)
    : IAlertControlResponse;
