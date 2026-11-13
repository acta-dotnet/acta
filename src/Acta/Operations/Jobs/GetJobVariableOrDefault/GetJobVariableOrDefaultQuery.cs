namespace Acta;

/// <summary>The job whose variable to read, and the variable's name as the caller spelled it.</summary>
internal sealed record GetJobVariableOrDefaultQuery(JobLookup Job, string Name);
