using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler that throws on its first attempt per namespace, then succeeds. With <c>MaxAttempts = 3</c> and
/// zero backoff a test drives: attempt 1 (fail -> re-arm, a non-terminal failure alert) then attempt 2
/// (success -> recovery). Proves the <c>sys.alerts</c> success branch resolves the open failure alert and
/// emits a single recovery.
/// </summary>
public static class FlakyRecoverProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("flaky-recover", MaxAttempts = 3, Backoff = "0s")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var attempt = _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        if (attempt == 1)
        {
            throw new InvalidOperationException("flaky-recover fails its first attempt.");
        }
    }
}
