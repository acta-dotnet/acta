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
        ResolveSlotIdHandler slots,
        IJobs jobs,
        string jobNamespace,
        CancellationToken ct
    ) =>
        await slots.HandleAsync(new ResolveSlotIdQuery(jobNamespace, JobName), ct) is { } id
            ? await jobs.GetAsync(JobLookup.ById(id), ct)
            : null;
}
