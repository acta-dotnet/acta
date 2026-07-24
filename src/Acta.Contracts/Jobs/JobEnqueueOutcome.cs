namespace Acta;

/// <summary>
/// Per-row outcome from <see cref="IJobs.EnqueueBatchAsync(System.Collections.Generic.IReadOnlyList{JobEnqueueRequest}, System.Threading.CancellationToken)"/>. Positionally aligned with the
/// caller's request list: <c>outcomes[i]</c> corresponds to <c>requests[i]</c>. Carries the
/// internal <see cref="JobId"/> and the public <see cref="JobRef"/>; callers read
/// <see cref="Action"/> to distinguish a fresh insert from a <see cref="JobEnqueueRequest.DeduplicationKey"/>-based dedup match.
/// </summary>
public sealed record JobEnqueueOutcome(long JobId, JobRef JobRef, JobEnqueueAction Action);

/// <summary>
/// Coarse outcome classifier for one enqueued row.
/// </summary>
public enum JobEnqueueAction : byte
{
    /// <summary>A fresh <c>job</c> row was inserted.</summary>
    Inserted = 1,

    /// <summary>An existing row matched the <see cref="JobEnqueueRequest.DeduplicationKey"/>; no new row was inserted.</summary>
    Deduplicated = 2,
}
