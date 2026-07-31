using System.Text.Json.Serialization;
using Acta;

namespace Anvil;

// The five stable job shapes behind Anvil's named workloads.

/// <summary>A quick job that always succeeds after an optional small delay; the steady baseline.</summary>
public sealed record SteadySuccess(string Label, int WorkMs = 250);

/// <summary>
/// A long job that succeeds, but slowly. These are the crash victims: while one is mid-flight its
/// worker can be killed, the lease lapses, maintenance reclaims it, and another worker re-runs it from
/// the top until it finishes.
/// </summary>
public sealed record SlowSuccess(string Label, int StepCount, int StepDelayMs);

/// <summary>A job that throws on its first attempt and succeeds on the retry; a transient glitch.</summary>
public sealed record FlakyOnce(string Label);

/// <summary>A job that always throws; it exhausts its retry budget and lands terminal Failed by design.</summary>
public sealed record AlwaysFails(string Label);

/// <summary>
/// A handler that does nothing: no work, no result, no durable ops. Draining a batch of these spikes
/// out the framework's own per-job latency (claim, execute, complete) with the handler cost removed.
/// </summary>
public sealed record NoOp(string Label);

public sealed record OutboxReceipt(string OperationId);

/// <summary>A parent job that fans out to <see cref="ChildCount"/> children and joins on all of them; the lineage demo.</summary>
public sealed record FanOut(string Label, int ChildCount);

public static class SteadySuccessJob
{
    [Job("steady-success")]
    public static async Task<string> Handle(SteadySuccess input, CancellationToken ct)
    {
        if (input.WorkMs > 0)
        {
            await Task.Delay(input.WorkMs, ct);
        }

        return $"ok: {input.Label}";
    }
}

public static class SlowSuccessJob
{
    // Default MaxAttempts (15) so a crash-reclaim-rerun never exhausts the budget: a stolen lease
    // re-arms the job, and we want it to keep trying until a surviving worker carries it home.
    [Job("slow-success")]
    public static async Task<string> Handle(SlowSuccess input, JobContext ctx, CancellationToken ct)
    {
        for (var step = 1; step <= input.StepCount; step++)
        {
            await Task.Delay(input.StepDelayMs, ct);
            await ctx.SetProgressAsync(step * 100 / input.StepCount, ct);
        }

        return $"done: {input.Label}";
    }
}

public static class FlakyOnceJob
{
    // A durable variable records that the first attempt ran. The write commits even though the attempt
    // then throws, so the retry sees it and succeeds: a transient failure the retry budget absorbs.
    [Job("flaky-once", MaxAttempts = 5, Backoff = "2s..4s")]
    public static async Task<string> Handle(FlakyOnce input, JobContext ctx, CancellationToken ct)
    {
        var primed = await ctx.ExistsVariableAsync("primed", ct);
        if (!primed)
        {
            await ctx.SetVariableAsync("primed", true, ct);
            throw new InvalidOperationException($"Transient glitch handling '{input.Label}' (succeeds on retry).");
        }

        return $"recovered: {input.Label}";
    }
}

public static class AlwaysFailsJob
{
    // A small budget so it reaches terminal Failed inside the demo window. This is the dead-letter
    // story: the framework retries, backs off, then stops and parks the job as Failed for an operator.
    [Job("always-fails", MaxAttempts = 3, Backoff = "2s..4s")]
    public static Task Handle(AlwaysFails input, CancellationToken ct) =>
        throw new InvalidOperationException($"Permanent failure processing '{input.Label}'.");
}

public static class NoOpJob
{
    // No result and no work: the framework's claim/execute/complete floor.
    [Job("noop")]
    public static void Handle(NoOp input) { }
}

public static class OutboxReceiptJob
{
    // Target of the outbox-pressure fault: rows staged into the producer database's acta_outbox
    // arrive here through sys.outbox. The work is trivial on purpose; provenance is the point.
    [Job("outbox-receipt")]
    public static void Handle(OutboxReceipt input) { }
}

public static class FanOutJob
{
    // Replay-safe fan-out: each StartChildAsync name is that child's dedup key, so a suspend/resume
    // replay of this loop returns the already-started children instead of inserting duplicates. Child
    // ids are rebuilt from the outcomes on every replay - no static or in-process state carries them.
    [Job("fan-out")]
    public static async Task<string> Handle(FanOut input, JobContext ctx, CancellationToken ct)
    {
        var childIds = new List<long>(input.ChildCount);
        for (var i = 0; i < input.ChildCount; i++)
        {
            var child = await ctx.StartChildAsync($"child-{i}", new SteadySuccess($"{input.Label}-child-{i}", 50), ct: ct);
            childIds.Add(child.JobId);
        }

        var outcomes = await ctx.WaitChildrenAsync(childIds, ct);
        var succeeded = outcomes.Count(outcome => outcome.Succeeded);
        return $"fan-out {input.Label}: {succeeded}/{outcomes.Count} children succeeded";
    }
}

/// <summary>Payload-less recurring pulse: keeps the schedules view alive even with no workload seeded.</summary>
public readonly record struct Pulse;

public static class PulseJob
{
    // Two independently moving schedule cursors on one recurring slot (an ISO interval plus a cron
    // sweep), so the dashboard always has live recurring executions to show.
    [Job("pulse")]
    [JobSchedule("pulse", "PT1M")]
    [JobSchedule("cron-sweep", Cron.Every5Minutes)]
    public static void Handle(Pulse input) { }
}

/// <summary>
/// Reflection-free payload builders for the lab's job inputs: each selects the source-generated
/// JsonTypeInfo from <see cref="AnvilPayloadJsonContext"/> so enqueue serialization needs no reflection
/// under Native AOT. Previews the shape EMIT will generate (an <c>ActaSerializer.Json(...)</c> helper) for
/// first-party apps; hand-written here for the Anvil proof, used wherever the lab builds a raw payload.
/// </summary>
internal static class AnvilPayloads
{
    public static JobPayload Json(SteadySuccess v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.SteadySuccess);

    public static JobPayload Json(SlowSuccess v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.SlowSuccess);

    public static JobPayload Json(FlakyOnce v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.FlakyOnce);

    public static JobPayload Json(AlwaysFails v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.AlwaysFails);

    public static JobPayload Json(NoOp v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.NoOp);

    public static JobPayload Json(OutboxReceipt v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.OutboxReceipt);

    public static JobPayload Json(FanOut v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.FanOut);
}

/// <summary>
/// Hand-written source-generated payload context for the lab's job types: every job input + output, plus
/// the durable-variable and progress scalar types. Wired via <c>j.UseJsonPayloads(AnvilPayloadJsonContext.Default)</c>
/// so payload (de)serialization needs no reflection under Native AOT. The wire-shape options mirror the
/// framework defaults (camelCase, string enums, nulls kept).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never
)]
// Job inputs.
[JsonSerializable(typeof(SteadySuccess))]
[JsonSerializable(typeof(SlowSuccess))]
[JsonSerializable(typeof(FlakyOnce))]
[JsonSerializable(typeof(AlwaysFails))]
[JsonSerializable(typeof(NoOp))]
[JsonSerializable(typeof(OutboxReceipt))]
[JsonSerializable(typeof(FanOut))]
[JsonSerializable(typeof(Pulse))]
// Job outputs.
[JsonSerializable(typeof(string))]
// Durable variable + progress scalars (FlakyOnce primes a bool; SlowSuccess reports int progress).
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal sealed partial class AnvilPayloadJsonContext : JsonSerializerContext;
