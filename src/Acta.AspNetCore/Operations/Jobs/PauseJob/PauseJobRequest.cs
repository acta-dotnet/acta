namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/pause</c>. A null <c>ExpectedVersion</c> applies the verb unconditionally; a
/// non-null one that misses the row's current version answers 409 with action versionConflict and writes nothing.
/// </summary>
internal sealed record PauseJobRequest(string? ReasonMessage = null, int? ExpectedVersion = null) : IControlRequest;
