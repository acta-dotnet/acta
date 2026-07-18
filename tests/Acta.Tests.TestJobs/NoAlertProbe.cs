using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A one-shot handler that always throws with <c>MaxAttempts = 1</c> (terminal Failed on the first tick)
/// and <c>AlertProfile = None</c>. Proves the <c>sys.alerts</c> projector emits nothing for a definition
/// that opts out of automatic alerts even though its job failed terminally.
/// </summary>
public static class NoAlertProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("no-alert-probe", MaxAttempts = 1, AlertProfile = JobAlertProfileCode.None)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        throw new InvalidOperationException("no-alert-probe always fails.");
    }
}
