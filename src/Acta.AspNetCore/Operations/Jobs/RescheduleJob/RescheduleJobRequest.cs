namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/reschedule</c>. <c>NextRunAtUtc</c> omitted or null means now, the job page's
/// "run now"; a default instant is a 400. <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
/// <param name="NextRunAtUtc">When the job should run, in UTC; omitted means now.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
/// <param name="ExpectedVersion">The job's version as the caller last read it; when the job has another version the call answers
/// version-conflict and changes nothing. Omit it to act on the job whatever its version.</param>
internal sealed record RescheduleJobRequest(DateTime? NextRunAtUtc = null, string? ReasonMessage = null, int? ExpectedVersion = null)
    : IControlRequest;
