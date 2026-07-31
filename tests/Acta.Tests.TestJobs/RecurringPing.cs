using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

public readonly record struct RecurringPing;

public sealed record RecurringPingResult(int Sequence);

/// <summary>
/// Recurring test job: one declared schedule, audit on, a small result cap so the ring-buffer trim
/// is cheap to exercise. Observation state is keyed by namespace so the SqlServer and Pg conformance
/// specs (distinct namespaces) never collide when run in parallel.
/// </summary>
public static class RecurringPingHandler
{
    // namespace -> the TriggeringScheduleNames seen on each fire, in order.
    public static readonly ConcurrentDictionary<string, List<IReadOnlyList<string>>> Triggers = new(StringComparer.Ordinal);

    // namespace -> fail every fire whose 1-based sequence is <= this value (drives the failure-budget specs).
    public static readonly ConcurrentDictionary<string, int> FailWhileSequenceAtMost = new(StringComparer.Ordinal);

    // namespace -> the handler calls ctx.CancelAsync on the fire whose 1-based sequence equals this
    // value (drives the recurring handler-control spec: a deliberate cancel stops the whole slot).
    public static readonly ConcurrentDictionary<string, int> CancelOnSequence = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        Triggers[jobNamespace] = [];
        FailWhileSequenceAtMost.TryRemove(jobNamespace, out _);
        CancelOnSequence.TryRemove(jobNamespace, out _);
    }

    public static IReadOnlyList<IReadOnlyList<string>> TriggersFor(string jobNamespace) =>
        Triggers.TryGetValue(jobNamespace, out var list) ? list : Array.Empty<IReadOnlyList<string>>();

    [Job("recurring-ping", AuditLevel = JobAuditLevelCode.Audit, RecurringResultCap = 3, MaxAttempts = 2)]
    [JobSchedule("every-5-minutes", Cron.Every5Minutes)]
    public static Task<RecurringPingResult> Run(RecurringPing input, JobContext ctx, CancellationToken ct)
    {
        var list = Triggers.GetOrAdd(ctx.JobNamespace, _ => []);
        int sequence;
        lock (list)
        {
            list.Add(ctx.TriggeringScheduleNames);
            sequence = list.Count;
        }

        if (FailWhileSequenceAtMost.TryGetValue(ctx.JobNamespace, out var failUntil) && sequence <= failUntil)
        {
            throw new InvalidOperationException($"forced recurring failure on sequence {sequence}");
        }

        return CancelOnSequence.TryGetValue(ctx.JobNamespace, out var cancelAt) && sequence == cancelAt
            ? throw new HandlerCancelException($"recurring slot stopped by handler on sequence {sequence}")
            : Task.FromResult(new RecurringPingResult(sequence));
    }
}
