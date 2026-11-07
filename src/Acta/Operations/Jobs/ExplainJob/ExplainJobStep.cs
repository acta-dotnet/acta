namespace Acta;

/// <summary>One step's state, with <see cref="Explanation"/> rendering it as a plain-English clause.</summary>
/// <param name="Name">The step's name, unique within its job.</param>
/// <param name="Status">The step's status.</param>
/// <param name="Explanation">The step's state in a plain-English clause.</param>
public sealed record ExplainJobStep(string Name, StepStatusCode Status, string Explanation);
