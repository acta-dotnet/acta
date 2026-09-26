using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A recurring slot declared at <c>AuditLevel.Failures</c> whose next fires can be told to throw, per
/// namespace. One attempt per fire, so a throw is the fire's failure and rolls the slot to its next
/// occurrence rather than retrying inside it.
/// </summary>
public static class FailuresAuditSlot
{
    private static readonly ConcurrentDictionary<string, int> _fires = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> _failingFires = new(StringComparer.Ordinal);

    /// <summary>Arms the slot to throw on its next <paramref name="failingFires"/> fires and zeroes the fire count.</summary>
    public static void Reset(string jobNamespace, int failingFires)
    {
        _fires[jobNamespace] = 0;
        _failingFires[jobNamespace] = failingFires;
    }

    public static int Fires(string jobNamespace) => _fires.TryGetValue(jobNamespace, out var n) ? n : 0;

    [Job("failures-audit-slot", MaxAttempts = 1, AuditLevel = JobAuditLevelCode.Failures)]
    [JobSchedule("default", Cron.Daily)]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var fire = _fires.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1);
        await Task.Yield();
        if (_failingFires.TryGetValue(ctx.JobNamespace, out var failing) && fire <= failing)
        {
            throw new InvalidOperationException($"failures-audit-slot fails fire {fire}.");
        }
    }
}
