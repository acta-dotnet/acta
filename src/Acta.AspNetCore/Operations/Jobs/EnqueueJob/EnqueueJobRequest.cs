using System.Text.Json;

namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs</c>. At most one of <c>Input</c> (raw JSON stored as json) or <c>Base64</c> (stored as the
/// binary format named by <c>FormatId</c>, which must be 2 or 128..255) supplies the input; both absent enqueues a
/// no-input job. <c>Priority</c> accepts the code string (e.g. "high").
/// </summary>
internal sealed record EnqueueJobRequest(
    string? JobNamespace = null,
    string? JobName = null,
    // Nullable rather than a bare JsonElement: the absent case has to be a value the schema generator
    // can write as this parameter's default, and an uninitialized JsonElement has nothing to write.
    JsonElement? Input = null,
    string? Base64 = null,
    byte? FormatId = null,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? TenantName = null,
    JobPriorityCode? Priority = null,
    int? DelaySeconds = null,
    DateTime? NextRunAtUtc = null
);
