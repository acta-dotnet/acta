namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /jobs/{jobRef}/purge</c>: an empty object, optional like the body itself. Purge records no reason.</summary>
internal sealed record PurgeJobRequest : IControlRequest
{
    string? IControlRequest.ReasonMessage => null;
}
