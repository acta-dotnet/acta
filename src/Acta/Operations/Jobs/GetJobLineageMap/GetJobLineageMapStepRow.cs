namespace Acta;

/// <summary>One <c>steps</c> row of the focused Job for the lineage read, in SELECT order.</summary>
[DbProjection]
internal sealed record GetJobLineageMapStepRow(string Name, StepStatusCode Status);
