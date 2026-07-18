using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A one-shot handler that always throws, with a small <c>MaxAttempts</c> and a zero initial backoff so
/// a test can drive each retry tick without waiting. Counts invocations per namespace to prove the retry
/// budget is honored (re-arm to Ready while in budget, terminal Failed once exhausted).
/// </summary>
public static class RetryProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("retry-probe", MaxAttempts = 3, Backoff = "0s")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        throw new InvalidOperationException("retry-probe always fails.");
    }
}
