namespace Acta;

/// <summary>
/// The result payload a job stored, returned by <see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>.
/// </summary>
public sealed record GetResultResponse(JobPayload Result);
