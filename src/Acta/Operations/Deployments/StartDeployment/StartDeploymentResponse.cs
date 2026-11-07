namespace Acta;

/// <summary>What a deploy decides on for each namespace it opened, read under that namespace's schedule lock.</summary>
internal readonly record struct StartDeploymentResponse(IReadOnlyList<StartDeploymentRow> Namespaces);
