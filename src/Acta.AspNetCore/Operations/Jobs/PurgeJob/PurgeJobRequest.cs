namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /jobs/{jobRef}/purge</c>, optional like the body itself: the reason the purge records.</summary>
internal sealed record PurgeJobRequest(string? ReasonMessage = null) : IControlRequest;
