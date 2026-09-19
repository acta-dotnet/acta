using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// Rate probes: one definition metered on its own name and two sharing an explicit key, each
/// recording the instant every admitted handler started per namespace. A spec reads those instants
/// back to check the realized rate; the handlers do no work, because what is being measured is when
/// admission let them in, not how long they took.
/// </summary>
public static class RateLimitProbes
{
    /// <summary>The declared rate of <c>rate-limited-probe</c>, so a spec asserts against the attribute.</summary>
    public const string Rate = "10/s";

    /// <summary>The declared rate the two shared-key definitions agree on.</summary>
    public const string SharedRate = "5/s";

    /// <summary>The key both shared-key definitions meter on.</summary>
    public const string SharedKey = "shared-meter";

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> Admissions = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => Admissions[jobNamespace] = new ConcurrentQueue<DateTime>();

    /// <summary>Every admitted start instant in this namespace, oldest first.</summary>
    public static IReadOnlyList<DateTime> AdmittedAt(string jobNamespace) =>
        Admissions.TryGetValue(jobNamespace, out var queue) ? [.. queue.OrderBy(static at => at)] : [];

    [Job("rate-limited-probe", RateLimit = Rate)]
    public static Task Run(JobContext ctx, CancellationToken ct)
    {
        Record(ctx.JobNamespace);
        return Task.CompletedTask;
    }

    /// <summary>The rate and slot count of <c>rate-and-slot-probe</c>, which declares both gates.</summary>
    public const string SlotProbeRate = "5/s";

    public const short SlotProbeLimit = 2;

    [Job("rate-and-slot-probe", ConcurrencyLimit = SlotProbeLimit, RateLimit = SlotProbeRate)]
    public static Task RunWithSlot(JobContext ctx, CancellationToken ct)
    {
        Record(ctx.JobNamespace);
        return Task.CompletedTask;
    }

    [Job("rate-shared-left", RateLimit = SharedRate, RateKey = SharedKey)]
    public static Task RunSharedLeft(JobContext ctx, CancellationToken ct)
    {
        Record(ctx.JobNamespace);
        return Task.CompletedTask;
    }

    [Job("rate-shared-right", RateLimit = SharedRate, RateKey = SharedKey)]
    public static Task RunSharedRight(JobContext ctx, CancellationToken ct)
    {
        Record(ctx.JobNamespace);
        return Task.CompletedTask;
    }

    private static void Record(string jobNamespace) =>
        Admissions.GetOrAdd(jobNamespace, static _ => new ConcurrentQueue<DateTime>()).Enqueue(DateTime.UtcNow);
}
