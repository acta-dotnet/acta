namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /jobs/{jobRef}/purge</c>, optional like the body itself: the reason the purge records.</summary>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record PurgeJobRequest(string? ReasonMessage = null) : IControlRequest;
