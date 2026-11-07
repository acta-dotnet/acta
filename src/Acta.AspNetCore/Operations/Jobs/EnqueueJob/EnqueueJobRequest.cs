namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /jobs</c>. <c>Input</c> is the payload envelope; absent or null enqueues a no-input job.
/// <c>Priority</c> accepts the code string (e.g. "high"). <c>Lane</c>, <c>ConcurrencyKey</c>, and <c>Tags</c> are the
/// metadata <c>IJobs.EnqueueAsync</c> takes, under its rules: a lane is a dotted kebab name and defaults to the
/// definition's, a key is case-exact printable ASCII, and tag names are unique once folded.
/// </summary>
internal sealed record EnqueueJobRequest(
    string? JobNamespace = null,
    string? JobName = null,
    JobPayloadBody? Input = null,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    string? TenantName = null,
    JobPriorityCode? Priority = null,
    int? DelaySeconds = null,
    DateTime? NextRunAtUtc = null,
    string? Lane = null,
    string? ConcurrencyKey = null,
    IReadOnlyList<TagInput>? Tags = null
);
