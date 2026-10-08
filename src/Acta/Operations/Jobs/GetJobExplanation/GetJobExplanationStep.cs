namespace Acta;

/// <summary>One step slot's state, with <see cref="Explanation"/> rendering it as a plain-English clause.</summary>
public sealed record GetJobExplanationStep(string Name, StepStatusCode Status, string Explanation);
