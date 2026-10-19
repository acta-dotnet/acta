namespace Acta;

/// <summary>The wait decision plus the stored payload when the outcome is ContinueSet.</summary>
[DbProjection]
internal sealed record WaitSignalResponse(WaitSignalOutcomeCode Outcome, byte ValueFormatId, byte[]? Value);
