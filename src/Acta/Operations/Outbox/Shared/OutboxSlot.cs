namespace Acta;

/// <summary>A namespace's <c>sys.outbox</c> slot job, the anchor of its source line and its command inbox.</summary>
internal static class OutboxSlot
{
    public const string JobName = "sys.outbox";

    /// <summary>
    /// The slot by its fixed deduplication key, gated on the job name so a user job that happens to reuse the
    /// key can never receive operator commands or spoof a source line.
    /// </summary>
    public static async ValueTask<GetJobResponse?> FindAsync(IJobs jobs, string jobNamespace, CancellationToken ct) =>
        await jobs.GetAsync(JobLookup.ByDeduplicationKey(jobNamespace, JobName), ct) is { JobName: JobName } slot ? slot : null;
}
