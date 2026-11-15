namespace Acta;

/// <summary>The job whose step result to read, and the step's name as the caller spelled it.</summary>
internal sealed record GetJobStepResultOrDefaultQuery(JobLookup Job, string Name);
