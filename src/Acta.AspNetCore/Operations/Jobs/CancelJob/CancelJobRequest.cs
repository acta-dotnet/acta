namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/cancel</c>. A null <c>ExpectedVersion</c> applies the verb unconditionally; a
/// non-null one that misses the row's current version answers 409 with action versionConflict and writes nothing.
/// </summary>
internal sealed record CancelJobRequest(string? ReasonMessage = null, int? ExpectedVersion = null) : IControlRequest;
