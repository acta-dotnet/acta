namespace Acta;

/// <summary>A job's current status, returned by <see cref="IJobs.GetStatusAsync"/>.</summary>
public sealed record GetJobStatusResponse(JobStatusCode Status);
