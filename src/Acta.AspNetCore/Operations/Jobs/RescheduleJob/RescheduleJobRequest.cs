namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/reschedule</c>. <c>NextRunAtUtc</c> omitted or null means now, the job page's
/// "run now"; a default instant is a 400. <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record RescheduleJobRequest(DateTime? NextRunAtUtc = null, string? ReasonMessage = null, int? ExpectedVersion = null)
    : IControlRequest;
