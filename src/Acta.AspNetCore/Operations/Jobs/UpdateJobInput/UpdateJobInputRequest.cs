using System.Text.Json;

namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs/{jobRef}/input</c>. Exactly one of <c>Input</c> (raw JSON, stored as json) or <c>Base64</c>
/// (stored as the job's current binary format) must be present. <c>Base64</c> must match a binary stored format;
/// <c>Input</c> is accepted for any non-none format.
/// </summary>
internal sealed record UpdateJobInputRequest(
    // Nullable rather than a bare JsonElement: the absent case has to be a value the schema generator
    // can write as this parameter's default, and an uninitialized JsonElement has nothing to write.
    JsonElement? Input = null,
    string? Base64 = null,
    string? ReasonMessage = null
);
