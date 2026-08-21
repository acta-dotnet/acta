using System.Globalization;
using System.Text;
using Acta.Relational.Entities;

namespace Acta.Testing.Hosting;

/// <summary>
/// The one implementation of "move a bounded group wait past its deadline", shared by
/// <see cref="IActaTestHost.ForceGroupWaitTimeoutDueAsync"/> and by the conformance specs that drive a
/// WorkerRuntime directly instead of a test host. Two copies of it could disagree about which rows a
/// staged expiry moves, and the failure mode is a test that reads as passing.
/// </summary>
internal static class WaitTimeoutStaging
{
    /// <summary>
    /// Rewrites every stored group deadline on <paramref name="jobId"/> to <paramref name="dueAtUtc"/>
    /// and moves every armed child latch to the same instant, which is what real elapsed time would do
    /// to both. Rewinding the latches alone would leave the deadline in the future and let a member
    /// armed after it start a fresh countdown. Returns how many group deadlines were moved, so the
    /// caller can decide whether finding none is an error. <paramref name="slotName"/> narrows the
    /// write to one group on a job holding several, which the public helper deliberately does not
    /// expose: an author has no way to know a slot's derived name.
    /// </summary>
    public static async Task<int> ForceGroupWaitDueAsync(
        IDbSession db,
        long jobId,
        DateTime dueAtUtc,
        CancellationToken ct,
        string? slotName = null
    )
    {
        var deadlines = await FindGroupDeadlinesAsync(db, jobId, slotName, ct);
        if (deadlines.Count == 0)
        {
            return 0;
        }

        // The bytes are written directly rather than through the payload serializer registry, because
        // this helper holds a db session and nothing else, and JSON for a long is its invariant decimal
        // digits either way. What the shortcut cannot see is a change of payload format on the sink, so
        // the format id is checked rather than assumed: a mismatch means the sink moved and this write
        // would quietly store bytes the runtime cannot read back.
        var value = Encoding.UTF8.GetBytes(dueAtUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var name in deadlines)
        {
            await db.From<JobCheckpoint>()
                .Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Variable && c.Name == name)
                .UpdateOnlyAsync(() => new JobCheckpoint { Value = value, ModifiedAtUtc = DbFn.UtcNow }, ct);
        }

        await db.From<JobCheckpoint>()
            .Where(c =>
                c.JobId == jobId
                && c.Kind == JobCheckpointKindCode.ChildLatch
                && c.Status == JobCheckpointStatusCode.Pending
                && c.DueAtUtc != null
            )
            .UpdateOnlyAsync(() => new JobCheckpoint { DueAtUtc = dueAtUtc, ModifiedAtUtc = DbFn.UtcNow }, ct);

        return deadlines.Count;
    }

    // Filtered in memory rather than in the query: the slot name is a prefix match on a framework-owned
    // key, and a job carries a handful of variables at most.
    private static async Task<IReadOnlyList<string>> FindGroupDeadlinesAsync(
        IDbSession db,
        long jobId,
        string? slotName,
        CancellationToken ct
    )
    {
        var variables = await db.From<JobCheckpoint>()
            .Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Variable)
            .ToListAsync(ct);
        var deadlines = variables
            .Where(c => c.Name.StartsWith(JobContext.GroupDeadlinePrefix, StringComparison.Ordinal))
            .Where(c => slotName is null || c.Name == slotName)
            .ToList();

        foreach (var slot in deadlines)
        {
            if (slot.ValueFormatId != JobPayloadFormat.Json.Id)
            {
                throw new InvalidOperationException(
                    $"Group deadline slot '{slot.Name}' on job {jobId} carries payload format {slot.ValueFormatId}, not JSON. "
                        + "The staging helper writes the deadline as JSON ticks and has to move with the sink that stores it."
                );
            }
        }

        return [.. deadlines.Select(c => c.Name)];
    }
}
