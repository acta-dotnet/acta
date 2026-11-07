using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

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

    // An at-most-once body that throws exhausts the step. The parent's default budget has attempts left, so the job
    // failing at once proves the uncaught StepExhaustedException is not retried.
    [Job("job-step-exhaust")]
    public static Task StepExhaust(JobContext ctx, CancellationToken ct) =>
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
