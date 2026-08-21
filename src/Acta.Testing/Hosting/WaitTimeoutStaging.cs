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
    /// caller can decide whether finding none is an error.
    /// </summary>
    public static async Task<int> ForceGroupWaitDueAsync(IDbSession db, long jobId, DateTime dueAtUtc, CancellationToken ct)
    {
        var deadlines = await FindGroupDeadlinesAsync(db, jobId, ct);
        if (deadlines.Count == 0)
        {
            return 0;
        }

        // The slot holds UTC ticks as JSON, which is exactly what JobContext's deadline sink writes.
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
    private static async Task<IReadOnlyList<string>> FindGroupDeadlinesAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var variables = await db.From<JobCheckpoint>()
            .Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Variable)
            .ToListAsync(ct);
        return [.. variables.Where(c => c.Name.StartsWith(JobContext.GroupDeadlinePrefix, StringComparison.Ordinal)).Select(c => c.Name)];
    }
}
