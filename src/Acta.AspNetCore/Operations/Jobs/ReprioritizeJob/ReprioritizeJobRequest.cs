using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>Body of the job's reprioritize: the new priority, and optionally a reason and an expected version.</summary>
/// <param name="Priority">The priority to give the job.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
/// <param name="ExpectedVersion">The job's version as the caller last read it; when the job has another version the call answers
/// version-conflict and changes nothing. Omit it to act on the job whatever its version.</param>
internal sealed record ReprioritizeJobRequest(
    [property: JsonRequired] JobPriorityCode Priority,
    string? ReasonMessage = null,
    long? ExpectedVersion = null
) : IControlRequest;
