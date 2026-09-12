using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler whose failing prefix is chosen per namespace: it throws while its attempt number is at or
/// below the configured count, then succeeds. Declared at <c>AuditLevel.Failures</c> with the default
/// <c>OnFailure</c> profile, the pairing under which a success writes no event at all.
/// </summary>
public static class FailuresAuditProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> _failWhileAttemptAtMost = new(StringComparer.Ordinal);

    /// <summary>Arms the probe to throw on its first <paramref name="failingAttempts"/> attempts.</summary>
    public static void Reset(string jobNamespace, int failingAttempts)
    {
        _attempts[jobNamespace] = 0;
        _failWhileAttemptAtMost[jobNamespace] = failingAttempts;
    }

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("failures-audit-probe", MaxAttempts = 6, Backoff = "0s", AuditLevel = JobAuditLevelCode.Failures)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var attempt = _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        if (_failWhileAttemptAtMost.TryGetValue(ctx.JobNamespace, out var failUntil) && attempt <= failUntil)
        {
            throw new InvalidOperationException($"failures-audit-probe fails attempt {attempt}.");
        }
    }
}
