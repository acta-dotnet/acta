namespace Acta;

/// <summary>The wait decision plus the stored payload when the outcome is ContinueSet.</summary>
[DbProjection]
internal sealed record WaitJobSignalResponse(WaitJobSignalOutcomeCode Outcome, byte ValueFormatId, byte[]? Value);
