using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>A lane probe step: <see cref="Lane"/> groups the concurrency count, <see cref="Label"/> is what the order records.</summary>
public sealed record LaneStep(string Lane, string Label);

/// <summary>Input for <c>lane-flaky</c>: fails its first <see cref="Failures"/> attempts, then succeeds.</summary>
public sealed record LaneFlakyStep(string Lane, string Label, int Failures);

/// <summary>Input for <c>lane-doomed</c>: fails every attempt.</summary>
public sealed record LaneDoomedStep(string Lane, string Label);

/// <summary>Input for <c>lane-pauser</c>: its handler pauses its own job.</summary>
public sealed record LanePauserStep(string Lane, string Label);

/// <summary>Input for <c>lane-defined</c>, whose definition declares the lane.</summary>
public sealed record LaneDefinedStep(string Label);

/// <summary>Input for <c>lane-parent-waits</c>: starts one child in <see cref="ChildLane"/> and waits for it.</summary>
public sealed record LaneParentStep(string ChildLane, string Label);

/// <summary>
/// Records, per namespace, the order lane probes ran in and the most handlers of one lane ever running at
/// once. A short dwell keeps each execution open long enough for an overlapping run of the same lane to be
/// counted.
/// </summary>
public static class LaneProbes
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Order = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Running = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> MaxSeen = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Attempts = new(StringComparer.Ordinal);

    /// <summary>The labels that ran in <paramref name="jobNamespace"/>, in start order; a flaky attempt reads as <c>label#n</c>.</summary>
    public static IReadOnlyList<string> Ran(string jobNamespace) => Order.TryGetValue(jobNamespace, out var q) ? [.. q] : [];

    /// <summary>The most handlers of one lane seen running at once in <paramref name="jobNamespace"/>.</summary>
    public static int MaxConcurrent(string jobNamespace) =>
        MaxSeen
            .Where(kv => kv.Key.StartsWith(jobNamespace + "\0", StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .DefaultIfEmpty(0)
            .Max();

    [Job("lane-step")]
    public static Task Step(LaneStep input, JobContext ctx, CancellationToken ct) =>
        RecordAsync(ctx.JobNamespace, input.Lane, input.Label, ct);

    [Job("lane-flaky", MaxAttempts = 5, Backoff = "0s")]
    public static async Task Flaky(LaneFlakyStep input, JobContext ctx, CancellationToken ct)
    {
        var attempt = Attempts.AddOrUpdate($"{ctx.JobNamespace}\0{input.Label}", 1, static (_, n) => n + 1);
        await RecordAsync(ctx.JobNamespace, input.Lane, $"{input.Label}#{attempt}", ct);
        if (attempt <= input.Failures)
        {
            throw new InvalidOperationException($"lane-flaky fails attempt {attempt}.");
        }
    }

    [Job("lane-doomed", MaxAttempts = 2, Backoff = "0s")]
    public static async Task Doomed(LaneDoomedStep input, JobContext ctx, CancellationToken ct)
    {
        await RecordAsync(ctx.JobNamespace, input.Lane, input.Label, ct);
        throw new InvalidOperationException("lane-doomed always fails.");
    }

    [Job("lane-pauser")]
    public static async Task Pauser(LanePauserStep input, JobContext ctx, CancellationToken ct)
    {
        await RecordAsync(ctx.JobNamespace, input.Lane, input.Label, ct);
        await ctx.PauseAsync("held by its handler", ct);
    }

    [Job("lane-defined", Lane = "defined-lane")]
    public static Task Defined(LaneDefinedStep input, JobContext ctx, CancellationToken ct) =>
        RecordAsync(ctx.JobNamespace, "defined-lane", input.Label, ct);

    [Job("lane-parent-waits")]
    public static async Task ParentWaits(LaneParentStep input, JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync(
            "child",
            new LaneStep(input.ChildLane, input.Label + "-child"),
            options => options.Lane(input.ChildLane),
            ct
        );
        await ctx.WaitChildAsync(child.JobId, ct);
    }

    private static async Task RecordAsync(string jobNamespace, string lane, string label, CancellationToken ct)
    {
        Order.GetOrAdd(jobNamespace, static _ => new ConcurrentQueue<string>()).Enqueue(label);
        var key = $"{jobNamespace}\0{lane}";
        var now = Running.AddOrUpdate(key, 1, static (_, n) => n + 1);
        MaxSeen.AddOrUpdate(key, now, (_, max) => Math.Max(max, now));
        try
        {
            await Task.Delay(20, ct);
        }
        finally
        {
            Running.AddOrUpdate(key, 0, static (_, n) => n - 1);
        }
    }
}
