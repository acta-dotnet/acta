namespace Acta;

/// <summary>What resolving an alert did, and its timestamps after the attempt.</summary>
public sealed record ResolveJobAlertManualResponse(
    AlertRef AlertRef,
    ControlAction Action,
    DateTime? AcknowledgedAtUtc,
    DateTime? ResolvedAtUtc
) : IAlertControlResponse;
