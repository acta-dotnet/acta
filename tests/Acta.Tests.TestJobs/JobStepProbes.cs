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
    // body. A signal (vs a sleep) makes the replay deterministic - the raise sets the slot.
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

    // In-budget retry: the body fails the first two invocations, then succeeds. Backoff is zero so the
    // parent re-arms immediately claimable; the parent failure budget must stay untouched.
    [Job("job-step-retry")]
    public static async Task StepRetry(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepAsync(
            "flaky",
            async _ =>
            {
                var n = RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                if (n < 3)
                {
                    throw new InvalidOperationException($"flaky failure #{n}");
                }
            },
            options => options.MaxAttempts(5).BackoffInitialDelay(TimeSpan.Zero),
            ct
        );
        await ctx.SetVariableAsync("step.done", true, ct);
    }

    // Exhaustion flows out as a normal handler failure. Parent MaxAttempts = 1 so the first uncaught
    // StepExhaustedException lands the parent terminally Failed.
    [Job("job-step-exhaust", MaxAttempts = 1)]
    public static Task StepExhaust(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "always-fails",
            async _ =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                throw new InvalidOperationException("boom");
            },
            options => options.MaxAttempts(2).BackoffInitialDelay(TimeSpan.Zero),
            ct
        );
}
