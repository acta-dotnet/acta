namespace Acta;

/// <summary>One step slot of the focused Job, with <see cref="Explanation"/> its status in plain English.</summary>
public sealed record GetJobLineageMapStep(string Name, StepStatusCode Status, string Explanation);
