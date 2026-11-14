using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What raising a signal on a job did, and the job's status after the attempt; a raise carries no version.</summary>
/// <param name="JobId">The job's internal id; the JSON omits it.</param>
/// <param name="Action">What the call did: applied; already-in-state, when the signal was raised before with this value; rejected, when the job is finished, the signal holds another value, or its wait expired, since a signal keeps its first value; or not-found.</param>
/// <param name="Status">The job's status after the call; null when the job was not found.</param>
public sealed record RaiseJobSignalResponse([property: JsonIgnore] long JobId, ControlAction Action, JobStatusCode? Status)
    : IJobControlResponse
{
    long? IControlResponse.Version => null;
}
