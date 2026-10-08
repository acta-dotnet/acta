using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/reprioritize</c>. <c>Priority</c> is mandatory, since its default is the lowest band;
/// a missing or unrecognized one fails deserialization (400). <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record ReprioritizeJobRequest(
    [property: JsonRequired] JobPriorityCode Priority,
    string? ReasonMessage = null,
    int? ExpectedVersion = null
) : IControlRequest;
