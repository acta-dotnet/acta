namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT /jobs/{jobRef}/input</c>: the payload envelope that replaces the job's input, required. A json input
/// replaces any stored format; base64 replaces only a binary-format input, and its <c>formatId</c> is the stored one.
/// </summary>
/// <param name="Input">The input to give the job, as a payload envelope.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
/// <param name="ExpectedVersion">The job's version as the caller last read it; when the job has another version the call answers
/// version-conflict and changes nothing. Omit it to act on the job whatever its version.</param>
internal sealed record UpdateJobInputRequest(JobPayloadBody? Input = null, string? ReasonMessage = null, long? ExpectedVersion = null)
    : IControlRequest;
