namespace Acta;

/// <summary>The ids of a job's direct children, terminal ones included.</summary>
internal sealed record GetChildJobIdsResponse(IReadOnlyList<long> ChildJobIds);
