namespace Acta;

/// <summary>One step of the focused job, with its status explained in plain English.</summary>
/// <param name="Name">The step's name, unique within its job.</param>
/// <param name="Status">The step's status.</param>
/// <param name="Explanation">The step's state in a plain-English clause.</param>
public sealed record GetJobLineageMapStep(string Name, StepStatusCode Status, string Explanation);
