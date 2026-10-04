namespace Acta.Runtime;

/// <summary>The job whose latest stored result to read.</summary>
internal sealed record GetJobResultQuery(JobLookup Job);
