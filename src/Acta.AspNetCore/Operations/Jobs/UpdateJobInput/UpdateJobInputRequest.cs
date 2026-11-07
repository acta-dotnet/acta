namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>PUT /jobs/{jobRef}/input</c>: the payload envelope that replaces the job's input, required. A json input
/// replaces any stored format; base64 replaces only a binary-format input, and its <c>formatId</c> is the stored one.
/// </summary>
internal sealed record UpdateJobInputRequest(JobPayloadBody? Input = null, string? ReasonMessage = null);
