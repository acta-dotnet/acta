namespace Acta;

/// <summary>The stored input payload of a job, as enqueued or last amended, returned by <see cref="IJobs.GetInputAsync"/>.</summary>
public sealed record GetJobInputResponse(JobPayload Input);
