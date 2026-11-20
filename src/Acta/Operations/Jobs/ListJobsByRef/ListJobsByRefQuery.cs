namespace Acta;

/// <summary>The refs to find the jobs of.</summary>
internal sealed record ListJobsByRefQuery(IReadOnlyList<Guid> JobRefs);
