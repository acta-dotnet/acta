namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/reschedule</c>. <c>NextRunAtUtc</c> is mandatory (missing or default is a 400);
/// <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record RescheduleJobRequest(DateTime NextRunAtUtc = default, string? ReasonMessage = null, int? ExpectedVersion = null)
    : IControlRequest;
