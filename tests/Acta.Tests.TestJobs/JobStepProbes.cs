using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>Input of the null-step reset probe: whether it resets its state as its last action.</summary>
public sealed record NullStepReset(bool ResetAtEnd);

/// <summary>
/// Durable-step probe handlers. Handlers restart from the top on every attempt; step bodies
/// count their invocations in <see cref="BodyInvocations"/> (keyed by job id, unique per run) so a
/// spec can prove a body ran exactly once across replays, or N times across retries.
/// </summary>
public static class JobStepProbes
{
    /// <summary>Per-job step-body invocation count. Keyed by job id so parallel specs don't collide.</summary>
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    private static int RecordInvocation(long jobId) => BodyInvocations.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    // Typed step, single tick: runs once and returns the stored value as the job result.
    [Job("job-step-basic")]
    public static Task<string> StepBasic(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "compute",
            async _ =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                return "ok";
            },
            ct: ct
        );

    // Void step: succeeds with no result payload.
    [Job("job-step-void")]
    public static async Task StepVoid(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepAsync(
            "side-effect",
            async _ =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
            },
            ct: ct
        );
        await ctx.SetVariableAsync("step.ran", true, ct);
    }

    // Replay: the step runs, then the handler waits on a signal. After the signal is raised the
    // handler replays from the top; the step must return its stored result WITHOUT re-invoking the
    // body. A signal (vs a sleep) makes the replay deterministic - the raise sets the step.
    [Job("job-step-replay")]
    public static async Task StepReplay(JobContext ctx, CancellationToken ct)
    {
        var value = await ctx.RunStepAsync(
            "once",
            async _ =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                return 7;
            },
            ct: ct
        );
        await ctx.WaitSignalAsync("proceed", ct);
        await ctx.SetVariableAsync("step.value", value, ct);
    }

    /// <summary>Per-job, per-step body invocation count, for a spec that tells two steps of one job apart.</summary>
    public static readonly ConcurrentDictionary<(long JobId, string Step), int> StepBodyInvocations = new();

    private static int RecordStepInvocation(long jobId, string step) =>
        StepBodyInvocations.AddOrUpdate((jobId, step), 1, static (_, n) => n + 1);

    // A wait inside an ordinary step: the first pass suspends at the wait, and the replay runs the body again up to
    // the wait, which returns because the signal is set.
    [Job("job-step-wait")]
    public static Task<string> StepWait(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "approve",
            async token =>
            {
                RecordInvocation(ctx.JobId);
                await ctx.WaitSignalAsync("approval", token);
                return "approved";
            },
            ct: ct
        );

    // A wait inside an ordinary step, then one failure past it. MaxAttempts = 2 leaves that failure a retry only when
    // the resume after the wait charged nothing.
    [Job("job-step-wait-then-fail", MaxAttempts = 2, Backoff = "0s")]
    public static Task StepWaitThenFail(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "approve",
            async token =>
            {
                var n = RecordInvocation(ctx.JobId);
                await ctx.WaitSignalAsync("approval", token);
                if (n == 2)
                {
                    throw new InvalidOperationException("transient failure past the wait");
                }
            },
            ct: ct
        );

    // Two steps; the second throws on its first invocation. The throw fails the attempt, and the retry replays the
    // first step from its stored outcome.
    [Job("job-step-throw", MaxAttempts = 3, Backoff = "0s")]
    public static async Task StepThrow(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepAsync(
            "reserve",
            _ =>
            {
                RecordStepInvocation(ctx.JobId, "reserve");
                return Task.CompletedTask;
            },
            ct
        );
        await ctx.RunStepAsync(
            "charge",
            _ =>
            {
                if (RecordStepInvocation(ctx.JobId, "charge") == 1)
                {
                    throw new InvalidOperationException("card declined once");
                }
                return Task.CompletedTask;
            },
            ct
        );
    }

    // A body that returns null records no result, and the execution after the raise replays it as null without its body.
    [Job("job-step-null-replay")]
    public static async Task<bool> StepNullReplay(JobContext ctx, CancellationToken ct)
    {
        var found = await ctx.RunStepAsync(
            "lookup",
            _ =>
            {
                RecordInvocation(ctx.JobId);
                return Task.FromResult<string?>(null);
            },
            ct
        );
        await ctx.WaitSignalAsync("proceed", ct);
        return found is null;
    }

    // The first execution records null as an int?, and the execution after the raise reads the step as an int, as a step
    // whose result type changed while the job was in flight does. The exception that ends the job is kept by its type name
    // in the variable "ended-by".
    [Job("job-step-null-value-type")]
    public static async Task StepNullValueType(JobContext ctx, CancellationToken ct)
    {
        if (ctx.ExecutionNumber == 1)
        {
            await ctx.RunStepAsync("count", _ => Task.FromResult<int?>(null), ct);
            await ctx.WaitSignalAsync("proceed", ct);
        }

        try
        {
            await ctx.RunStepAsync("count", _ => Task.FromResult(1), ct);
        }
        catch (Exception ex) when (ctx.EndsJob(ex))
        {
            await ctx.SetVariableAsync("ended-by", ex.GetType().Name, ct);
            throw;
        }
    }

    // An at-most-once body that returns null: the step is recorded done, so a restart replays it rather than finding it
    // interrupted.
    [Job("job-step-null-at-most-once")]
    public static async Task<bool> StepNullAtMostOnce(JobContext ctx, CancellationToken ct) =>
        await ctx.RunStepAsync(
            "lookup",
            _ =>
            {
                RecordInvocation(ctx.JobId);
                return Task.FromResult<string?>(null);
            },
            options => options.AtMostOnce(),
            ct
        )
            is null;

    // A body that returns null, in a job that, when its input says so, resets its state as its last action.
    [Job("job-step-null-reset")]
    public static async Task<bool> StepNullReset(NullStepReset input, JobContext ctx, CancellationToken ct)
    {
        var found = await ctx.RunStepAsync(
            "lookup",
            _ =>
            {
                RecordInvocation(ctx.JobId);
                return Task.FromResult<string?>(null);
            },
            ct
        );
        if (input.ResetAtEnd)
        {
            await ctx.ResetStateAsync(ct);
        }
        return found is null;
    }

    // A body that returns a non-null value whose JSON is null records no result, and the replay after the raise reads null.
    [Job("job-step-json-null")]
    public static async Task<bool> StepJsonNull(JobContext ctx, CancellationToken ct)
    {
        var value = await ctx.RunStepAsync(
            "lookup",
            _ =>
            {
                RecordInvocation(ctx.JobId);
                return Task.FromResult<JsonNullValue?>(new JsonNullValue(1));
            },
            ct
        );
        await ctx.WaitSignalAsync("proceed", ct);
        return value is null;
    }

    // The unserializable body below in an at-most-once step, which a restart then finds interrupted.
    [Job("job-step-unserializable-result-at-most-once")]
    public static Task StepUnserializableResultAtMostOnce(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync("unrecorded", _ => UnrecordedAsync(ctx, new UnserializableOutput(ctx.JobId)), options => options.AtMostOnce(), ct);

    /// <summary>The length of the text the oversize step probes return: past the small cap a spec sets.</summary>
    public const int OversizeStepResultLength = 4096;

    // A step whose result is past the cap a spec sets, which the job returns as its own.
    [Job("job-step-oversize-result")]
    public static Task<string> StepOversizeResult(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync("oversize", _ => OversizeAsync(ctx), ct);

    // The same step, then a wait on a signal: the execution after the raise replays the step from its stored result.
    [Job("job-step-oversize-result-replay")]
    public static async Task<string> StepOversizeResultReplay(JobContext ctx, CancellationToken ct)
    {
        var value = await ctx.RunStepAsync("oversize", _ => OversizeAsync(ctx), ct);
        await ctx.WaitSignalAsync("proceed", ct);
        return value;
    }

    private static Task<string> OversizeAsync(JobContext ctx)
    {
        RecordInvocation(ctx.JobId);
        return Task.FromResult(PayloadProbes.Text(OversizeStepResultLength));
    }

    // A body that finishes and returns what cannot be serialized. The default budget has attempts left, so the body
    // running once proves the failure is not retried.
    [Job("job-step-unserializable-result")]
    public static async Task<long> StepUnserializableResult(JobContext ctx, CancellationToken ct) =>
        (await ctx.RunStepAsync("unrecorded", _ => UnrecordedAsync(ctx, new UnserializableOutput(ctx.JobId)), ct)).Value;

    // The compensation idiom of the developer guide, around a step whose result cannot be recorded.
    [Job("job-step-unserializable-result-compensated")]
    public static async Task StepUnserializableResultCompensated(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.RunStepAsync("unrecorded", _ => UnrecordedAsync(ctx, new UnserializableOutput(ctx.JobId)), ct);
        }
        catch (Exception ex) when (ctx.EndsJob(ex))
        {
            await ctx.RunStepAsync("compensate", _ => Task.CompletedTask, ct);
            throw;
        }
    }

    private static Task<TResult> UnrecordedAsync<TResult>(JobContext ctx, TResult result)
    {
        RecordInvocation(ctx.JobId);
        return Task.FromResult(result);
    }

    // A body that resets the job's state deletes its own step's row, so the step's completion finds nothing to complete.
    [Job("job-step-reset-inside", MaxAttempts = 1)]
    public static Task StepResetInside(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync("resets", stepCt => ctx.ResetStateAsync(stepCt), ct);

    // An at-most-once body that throws fails the step. The parent's default budget has attempts left, so the job
    // failing at once proves the uncaught StepFailedException is not retried.
    [Job("job-step-fail")]
    public static Task StepFail(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "always-fails",
            async _ =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                throw new InvalidOperationException("boom");
            },
            options => options.AtMostOnce(),
            ct
        );
}
