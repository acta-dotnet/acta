namespace Acta;

/// <summary>
/// What one enqueue answers: the internal <see cref="JobId"/>, the public <see cref="JobRef"/>, and the
/// <see cref="Action"/> that tells a fresh insert from a match on <see cref="EnqueueJobRequest.DeduplicationKey"/>. A
/// batch answers one per request, in request order, in <see cref="EnqueueJobsResponse.Jobs"/>.
/// </summary>
public sealed record EnqueueJobResponse(long JobId, JobRef JobRef, EnqueueJobAction Action);
