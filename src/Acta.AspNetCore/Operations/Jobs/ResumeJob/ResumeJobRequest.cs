namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/resume</c>. A null <c>ExpectedVersion</c> applies the verb unconditionally; a
/// non-null one that misses the row's current version answers 409 with action version-conflict and writes nothing.
/// </summary>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
/// <param name="ExpectedVersion">The job's version as the caller last read it; when the job has another version the call answers
/// version-conflict and changes nothing. Omit it to act on the job whatever its version.</param>
internal sealed record ResumeJobRequest(string? ReasonMessage = null, int? ExpectedVersion = null) : IControlRequest;
