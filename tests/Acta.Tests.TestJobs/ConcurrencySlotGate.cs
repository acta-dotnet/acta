using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler under a concurrency limit of four that keeps its concurrency slot until the test releases it, so the
/// admissions racing it resolve while every admitted attempt still holds its slot. Per-namespace state keeps parallel
/// runs isolated.
/// </summary>
public static class ConcurrencySlotGate
{
    /// <summary>The declared limit, so a spec asserts against the attribute rather than a repeated literal.</summary>
    public const int Limit = 4;

    private static readonly ConcurrentDictionary<string, int> EnteredCount = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Releases = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        EnteredCount[jobNamespace] = 0;
        Releases[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>How many attempts have entered the handler.</summary>
    public static int Entered(string jobNamespace) => EnteredCount.GetValueOrDefault(jobNamespace);

    /// <summary>Lets every held attempt return, so each completes Succeeded and frees its slot.</summary>
    public static void Release(string jobNamespace) => Releases[jobNamespace].TrySetResult();

    [Job("concurrency-slot-gate", ConcurrencyLimit = Limit)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        EnteredCount.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Releases[ctx.JobNamespace].Task.WaitAsync(ct);
    }
}
