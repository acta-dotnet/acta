namespace Acta;

/// <summary>One step slot's state, with <see cref="Explanation"/> rendering it as a plain-English clause.</summary>
public sealed record ExplainJobStep(string Name, StepStatusCode Status, string Explanation);
