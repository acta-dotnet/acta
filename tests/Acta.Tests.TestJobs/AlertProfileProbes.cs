using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// Always-failing probe with <c>MaxAttempts = 2</c> and <c>AlertProfile = OnTerminal</c>. Non-terminal
/// failures are suppressed by the profile; only the terminal transition emits a FinalFailure alert.
/// </summary>
public static class OnTerminalProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("on-terminal-probe", MaxAttempts = 2, Backoff = "0s", AlertProfile = AlertProfileCode.OnTerminal)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        throw new InvalidOperationException("on-terminal-probe always fails.");
    }
}

/// <summary>
/// Always-failing probe with <c>MaxAttempts = 2</c> and <c>AlertProfile = Info</c>. Non-terminal
/// failures are suppressed; the terminal transition emits a FinalFailure alert at Info severity.
/// </summary>
public static class InfoAlertProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("info-alert-probe", MaxAttempts = 2, Backoff = "0s", AlertProfile = AlertProfileCode.Info)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        throw new InvalidOperationException("info-alert-probe always fails.");
    }
}

/// <summary>
/// Always-failing probe with <c>MaxAttempts = 2</c> and <c>AlertProfile = SysCritical</c>. Both
/// non-terminal and terminal failures emit alerts at Critical severity.
/// </summary>
public static class SysCriticalProbe
{
    private static readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace) => _attempts[jobNamespace] = 0;

    public static int Attempts(string jobNamespace) => _attempts.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("sys-critical-probe", MaxAttempts = 2, Backoff = "0s", AlertProfile = AlertProfileCode.SysCritical)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _attempts.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        throw new InvalidOperationException("sys-critical-probe always fails.");
    }
}
