namespace Acta;

/// <summary>The job whose latest stored result to read.</summary>
internal sealed record GetResultQuery(JobLookup Job);
