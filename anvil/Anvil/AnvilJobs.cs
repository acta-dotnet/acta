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

/// <summary>
/// A job whose one effect must never repeat: the charge shape. Its step is <c>AtMostOnce</c>, so a kill
/// mid-body does not re-run it - the replay terminalizes the ambiguity instead. These are seeded into the
/// crash workload precisely so the kills land inside that window.
/// </summary>
public sealed record AtMostOnceCharge(string Label, int WorkMs);

/// <summary>
/// A job whose definition declares a rate limit, so the engine meters how often its attempts may start
/// cluster-wide. Seeded into the crash workload so the meter is certified while workers are being killed
/// and their attempts reclaimed and re-run.
/// </summary>
public sealed record Metered10(string Label);

/// <summary>The <see cref="Metered10"/> shape at fifty admissions a second.</summary>
public sealed record Metered50(string Label);

/// <summary>The <see cref="Metered10"/> shape at a hundred admissions a second.</summary>
public sealed record Metered100(string Label);

/// <summary>
/// A job enqueued into a lane: the lane runs its members one at a time in enqueue order, and a killed
/// head is reclaimed and re-run in place before the member behind it may start. Seeded into the crash
/// workload so lane order is certified while the workers running the heads are being killed.
/// </summary>
public sealed record Laned(string Label, int StepCount, int StepDelayMs);

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
    // Durable steps, not a plain loop: these are the crash victims, and the whole point of killing
    // their worker is to show that recorded steps do not run twice. A loop would re-run everything
    // from the top and prove nothing.
    //
    // Each body writes one note before doing its work. That note is the witness certification reads:
    // `steps` keeps only current state (one row per (job, name), UPDATE-in-place), so without it a
    // body that ran again leaves no trace at all. A second note is legal on its own - at-least-once
    // means an interrupted body re-runs - but a note timestamped AFTER the step's own success is not,
    // and that is the violation the seal looks for.
    [Job("slow-success")]
    public static async Task<string> Handle(SlowSuccess input, JobContext ctx, CancellationToken ct)
    {
        // Tenant witness, one per attempt. Comparing events.tenant_id to jobs.tenant_id would be
        // tautological - two projections of one stored value - so the only non-circular question is
        // what the HANDLER saw after the whole enqueue, claim and dispatch path, which is this.
        await ctx.NoteAsync($"tenant {ctx.TenantKey ?? "-"}", ct);

        for (var step = 1; step <= input.StepCount; step++)
        {
            var name = $"step-{step}";
            await ctx.RunStepAsync(
                name,
                async token =>
                {
                    await ctx.NoteAsync($"step-body {name}", token);
                    await Task.Delay(input.StepDelayMs, token);
                },
                ct: ct
            );
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

public static class AtMostOnceChargeJob
{
    // The double-spend probe, and the one claim the seal could not previously make. The body writes a
    // note before doing its work, and the note is its own operation rather than part of the step's
    // transaction - which is the whole reason it survives to be evidence when the step does not.
    //
    // Two notes for one job would mean the body ran twice under an AtMostOnce contract that says it
    // runs zero or one times. Zero notes is legal and common: the kill landed before the body was
    // admitted. So the assertion is "never more than one", not "exactly one".
    //
    // Ending Failed is by design here, not a defect: a body interrupted before its outcome committed
    // terminalizes as ambiguous and throws StepInterruptedException, because for a charge, an honest
    // "this may have happened once" beats a confident retry. certify.sql exempts this shape from the
    // expected-outcome check for that reason and names it there.
    [Job("at-most-once-charge", MaxAttempts = 3, Backoff = "2s..4s")]
    public static async Task<string> Handle(AtMostOnceCharge input, JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync($"tenant {ctx.TenantKey ?? "-"}", ct);
        await ctx.RunStepAsync(
            "charge",
            async token =>
            {
                await ctx.NoteAsync("charge-body", token);
                await Task.Delay(input.WorkMs, token);
            },
            options => options.AtMostOnce(),
            ct
        );

        return $"charged: {input.Label}";
    }
}

public static class MeteredJob
{
    /// <summary>
    /// The three meters, each its own definition, read back by the verdict to derive the window budgets it
    /// checks per definition. Three rates metering side by side also prove each meter admits against its
    /// own bucket, never another's turns.
    /// </summary>
    internal static readonly (string JobName, string Rate, Func<string, JobPayload> Payload)[] Meters =
    [
        (Slow, SlowRate, label => AnvilPayloads.Json(new Metered10(label))),
        (Medium, MediumRate, label => AnvilPayloads.Json(new Metered50(label))),
        (Fast, FastRate, label => AnvilPayloads.Json(new Metered100(label))),
    ];

    private const string Slow = "metered-10";
    private const string SlowRate = "10/s";
    private const string Medium = "metered-50";
    private const string MediumRate = "50/s";
    private const string Fast = "metered-100";
    private const string FastRate = "100/s";

    // The note is the whole evidence for the rate contract, and it is written before anything else so
    // it stands for the instant the handler was admitted. Admission is metered per attempt, after
    // start_execution and before this body, so a reclaimed attempt that runs again is metered again -
    // which makes admitted handler starts the quantity the contract bounds. `steps` cannot count them:
    // it keeps one row per (job, name) and updates it in place. Every event carries the database's own
    // stamp, so the windows are measured on one clock rather than on each worker's.
    [Job(Slow, RateLimit = SlowRate)]
    public static Task<string> HandleSlow(Metered10 input, JobContext ctx, CancellationToken ct) => AdmitAsync(input.Label, ctx, ct);

    [Job(Medium, RateLimit = MediumRate)]
    public static Task<string> HandleMedium(Metered50 input, JobContext ctx, CancellationToken ct) => AdmitAsync(input.Label, ctx, ct);

    [Job(Fast, RateLimit = FastRate)]
    public static Task<string> HandleFast(Metered100 input, JobContext ctx, CancellationToken ct) => AdmitAsync(input.Label, ctx, ct);

    private static async Task<string> AdmitAsync(string label, JobContext ctx, CancellationToken ct)
    {
        await ctx.NoteAsync("metered-admitted", ct);
        // Long enough that a kill can land inside an admitted attempt, whose reclaim is then metered again.
        await Task.Delay(MeteredDwellMs, ct);
        return $"metered: {label}";
    }

    private const int MeteredDwellMs = 300;
}

public static class LanedJob
{
    // Seeded ten to a lane, so the lane runs them one at a time in enqueue order while workers die under
    // them. Durable steps with a body long enough that a kill lands inside one, and each step writes the
    // same step-body note slow-success does, so check 7 covers a replayed lane head too. The order itself
    // needs no witness: certify.sql reads it from the execution events the engine writes (checks 15-16).
    [Job("laned")]
    public static async Task<string> Handle(Laned input, JobContext ctx, CancellationToken ct)
    {
        for (var step = 1; step <= input.StepCount; step++)
        {
            var name = $"step-{step}";
            await ctx.RunStepAsync(
                name,
                async token =>
                {
                    await ctx.NoteAsync($"step-body {name}", token);
                    await Task.Delay(input.StepDelayMs, token);
                },
                ct: ct
            );
        }

        return $"laned: {input.Label}";
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

    public static JobPayload Json(AtMostOnceCharge v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.AtMostOnceCharge);

    public static JobPayload Json(Metered10 v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.Metered10);

    public static JobPayload Json(Metered50 v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.Metered50);

    public static JobPayload Json(Metered100 v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.Metered100);

    public static JobPayload Json(Laned v) => JobPayload.Json(v, AnvilPayloadJsonContext.Default.Laned);
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
[JsonSerializable(typeof(AtMostOnceCharge))]
[JsonSerializable(typeof(Metered10))]
[JsonSerializable(typeof(Metered50))]
[JsonSerializable(typeof(Metered100))]
[JsonSerializable(typeof(Laned))]
[JsonSerializable(typeof(Pulse))]
// Job outputs.
[JsonSerializable(typeof(string))]
// Durable variable + progress scalars (FlakyOnce primes a bool; SlowSuccess reports int progress).
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal sealed partial class AnvilPayloadJsonContext : JsonSerializerContext;
