namespace Acta;

/// <summary>The job whose checkpoint slots to read.</summary>
internal sealed record GetCheckpointsQuery(JobLookup Job);
