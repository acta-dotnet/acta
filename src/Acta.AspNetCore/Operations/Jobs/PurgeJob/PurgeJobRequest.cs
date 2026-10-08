namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /jobs/{jobRef}/purge</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record PurgeJobRequest(string? ReasonMessage = null) : IControlRequest;
