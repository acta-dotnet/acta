using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What an enqueue answers: the job's ref, and the action that tells a new job from a match on the deduplication key.</summary>
/// <remarks>A batch answers one per request, in request order; a typed enqueue answers the derived <see cref="JobHandle"/>.</remarks>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="JobRef">The ref of the job the call enqueued or matched.</param>
/// <param name="Action">What the call did: enqueued a new job, or matched an existing one by its deduplication key.</param>
public record EnqueueJobResponse([property: JsonIgnore] long JobId, JobRef JobRef, EnqueueJobAction Action);
