namespace Acta;

/// <summary>The payload a job's handler stored as one variable, in the format it was stored in.</summary>
internal sealed record GetJobVariableOrDefaultResponse(JobPayload Value);
