namespace Acta.AspNetCore;

/// <summary>
/// Body of the enqueue: which definition to run, its input, and the keys, priority, lane, timing, and tags the job takes.
/// </summary>
/// <param name="JobNamespace">The namespace of the definition to enqueue.</param>
/// <param name="JobName">The name of the definition to enqueue, within jobNamespace.</param>
/// <param name="Input">The job's input as a payload envelope; omitted or null enqueues a job without input.</param>
/// <param name="DeduplicationKey">A key that makes the enqueue repeat-safe within the namespace: a second enqueue with the same key
/// matches the first job and enqueues nothing.</param>
/// <param name="CorrelationKey">A key of the caller's that ties this job to related work; kept with its case and copied onto the job's
/// events.</param>
/// <param name="TenantName">The tenant the job is about; it must be registered and not suspended.</param>
/// <param name="Priority">The job's priority; omitted, the job takes its definition's.</param>
/// <param name="DelaySeconds">How many seconds to wait before the job first runs; send this or nextRunAtUtc, not both.</param>
/// <param name="NextRunAtUtc">When the job should first run, in UTC; omitted, it runs as soon as a worker claims it.</param>
/// <param name="Lane">The lane to run the job in; omitted, the job takes its definition's lane when it declares one.</param>
/// <param name="ConcurrencyKey">A key that limits how many jobs sharing it run at once; omitted, the definition's name is the
/// key.</param>
/// <param name="Tags">Tags to set on the job as it is enqueued.</param>
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
