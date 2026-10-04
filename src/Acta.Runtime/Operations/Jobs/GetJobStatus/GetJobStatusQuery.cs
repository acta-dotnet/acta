namespace Acta.Runtime;

/// <summary>The job whose status to read.</summary>
internal sealed record GetJobStatusQuery(JobLookup Job);
