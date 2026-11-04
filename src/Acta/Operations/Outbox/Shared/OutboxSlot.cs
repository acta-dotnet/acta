namespace Acta;

/// <summary>A namespace's <c>sys.outbox</c> slot job, the anchor of its source line and its command inbox.</summary>
internal static class OutboxSlot
{
    public const string JobName = "sys.outbox";

    /// <summary>
    /// The slot through its definition's path and its kind, so no user job can receive operator commands or spoof a
    /// source line.
    /// </summary>
    public static async ValueTask<GetJobResponse?> FindAsync(
        GetJobIdBySlotHandler slots,
        IJobs jobs,
        string jobNamespace,
        CancellationToken ct
    ) =>
        await slots.HandleAsync(new GetJobIdBySlotQuery(jobNamespace, JobName), ct) is { } slot
            ? await jobs.GetAsync(JobLookup.ById(slot.JobId), ct)
            : null;
}
