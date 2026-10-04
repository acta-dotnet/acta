namespace Acta.Runtime;

/// <summary>The job whose checkpoint slots to read.</summary>
internal sealed record GetJobCheckpointsQuery(JobLookup Job);
