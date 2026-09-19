using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// Concurrency probe for a definition that declares <c>ConcurrencyLimit</c>: counts how many handler
/// instances run at once per namespace and remembers the maximum observed. A short dwell keeps each
/// execution window open long enough that an extra admission would be observed as a higher maximum.
/// </summary>
public static class ConcurrencyLimitProbe
{
    /// <summary>The declared limit, so a spec asserts against the attribute rather than a repeated literal.</summary>
    public const short Limit = 2;

    private static readonly ConcurrentDictionary<string, int> Running = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> MaxSeen = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        Running[jobNamespace] = 0;
        MaxSeen[jobNamespace] = 0;
    }

    public static int MaxObserved(string jobNamespace) => MaxSeen.GetValueOrDefault(jobNamespace);

    [Job("concurrency-limit-probe", ConcurrencyLimit = Limit)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var now = Running.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        MaxSeen.AddOrUpdate(ctx.JobNamespace, now, (_, max) => Math.Max(max, now));
        try
        {
            await Task.Delay(50, ct);
        }
        finally
        {
            Running.AddOrUpdate(ctx.JobNamespace, 0, static (_, n) => n - 1);
        }
    }
}
