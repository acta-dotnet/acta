namespace Acta;

/// <summary>
/// What a batched enqueue answers: one <see cref="EnqueueJobResponse"/> per request, positionally aligned, so
/// <c>Jobs[i]</c> answers <c>requests[i]</c>.
/// </summary>
public sealed record EnqueueJobsResponse(IReadOnlyList<EnqueueJobResponse> Jobs);
