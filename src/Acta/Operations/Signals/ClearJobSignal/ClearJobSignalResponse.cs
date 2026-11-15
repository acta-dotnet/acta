using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What clearing a signal from a job did, and the job's status after the attempt; a clear carries no version.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it.</param>
/// <param name="Action">What the call did: applied; already-in-state, when the job holds no such signal; rejected, when the job has not finished and is not a permanent job between firings, or is a system job; or not-found.</param>
/// <param name="Status">The job's status after the call; null when the job was not found.</param>
/// <param name="Reason">Why a rejected clear was rejected: job-not-finished or system-job; null otherwise.</param>
public sealed record ClearJobSignalResponse(
    [property: JsonIgnore] long JobId,
    ControlAction Action,
    JobStatusCode? Status,
    string? Reason = null
) : IJobControlResponse
{
    long? IControlResponse.Version => null;
}
