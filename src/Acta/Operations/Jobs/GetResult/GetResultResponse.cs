namespace Acta;

/// <summary>
/// The latest result payload a job produced, returned by <see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>.
/// </summary>
public sealed record GetResultResponse(JobPayload Result);
