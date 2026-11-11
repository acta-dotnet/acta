namespace Acta.AspNetCore;

/// <summary>Optional body of the job's cancel: a reason, and the expected version that makes the call a compare-and-set.</summary>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
/// <param name="ExpectedVersion">The job's version as the caller last read it; when the job has another version the call answers
/// version-conflict and changes nothing. Omit it to act on the job whatever its version.</param>
internal sealed record CancelJobRequest(string? ReasonMessage = null, long? ExpectedVersion = null) : IControlRequest;
