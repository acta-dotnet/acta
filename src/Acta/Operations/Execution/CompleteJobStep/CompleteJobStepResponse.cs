namespace Acta;

/// <summary>The completion decision.</summary>
[DbProjection]
internal sealed record CompleteJobStepResponse(CompleteJobStepOutcomeCode Outcome);
