using System.Collections.Concurrent;
using System.Collections.Immutable;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Spec-local body-invocation counter: keyed by job id so parallel tests don't collide.
/// </summary>
internal static class StepExhaustionProbes
{
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    private static readonly ConcurrentDictionary<long, int> Exhaustions = new();

    public static int RecordInvocation(long jobId) => BodyInvocations.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    // Job 1: step MaxAttempts=2, zero-backoff, always fails. The handler catches the first exhaustion and reschedules,
    // so the replay re-enters the exhausted slot; the second exhaustion goes uncaught.
    // Proves: exhausted slot re-entry throws WITHOUT re-running the body.
    public static async Task ExhaustReplay(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.RunStepAsync(
                "always-fails",
                async bodyCt =>
                {
                    RecordInvocation(ctx.JobId);
                    await Task.CompletedTask;
                    throw new InvalidOperationException("step body always fails");
                },
                o => o.MaxAttempts(2).BackoffInitialDelay(TimeSpan.Zero),
                ct
            );
        }
        catch (StepExhaustedException) when (Exhaustions.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1) == 1)
        {
            await ctx.RescheduleAsync(TimeSpan.Zero, ct: ct);
        }
    }

    // Job 2: parent MaxAttempts=1; step MaxAttempts=100, RetryWindow=5s, initial backoff delay=30s.
    // Proves: window exhaustion (30s > 5s window) fires after attempt 1, far below MaxAttempts=100.
    public static async Task WindowExhaust(JobContext ctx, CancellationToken ct) =>
        await ctx.RunStepAsync(
            "window-fails",
            async bodyCt =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                throw new InvalidOperationException("step body always fails");
            },
            o => o.MaxAttempts(100).RetryWindow(TimeSpan.FromSeconds(5)).BackoffInitialDelay(TimeSpan.FromSeconds(30)),
            ct
        );
}

/// <summary>
/// Hand-written manifest for the two exhaustion-probe jobs. Kept isolated from
/// <c>TestJobsManifest</c> so the shared step counters and job definitions are unaffected.
/// </summary>
public sealed class StepExhaustionManifest : IJobManifest
{
    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: "step-exhaust-replay",
                HandlerType: typeof(StepExhaustionProbes),
                MethodName: nameof(StepExhaustionProbes.ExhaustReplay),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                AuditLevel: DefinitionAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await StepExhaustionProbes.ExhaustReplay(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            )
            {
                Backoff = "0s",
            },
            new JobDescriptor(
                JobName: "step-window-exhaust",
                HandlerType: typeof(StepExhaustionProbes),
                MethodName: nameof(StepExhaustionProbes.WindowExhaust),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 1,
                AuditLevel: DefinitionAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await StepExhaustionProbes.WindowExhaust(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            ),
        ]);
}

/// <summary>
/// Conformance for step exhaustion guarantees beyond attempt-count: window exhaustion fires
/// before MaxAttempts is reached, re-entering an exhausted slot throws without running the body, and an
/// operator restart gives the slot a fresh budget, so the body runs again.
/// </summary>
[ConformanceSpec(
    "step.exhaustion",
    "Step exhausts by retry-window and re-entry replays without body invocation",
    Area = "Steps",
    Contract = "A step exhausts when a retry would exceed its window, re-entering the exhausted slot throws without running the body, and a restart runs it afresh."
)]
public sealed class StepExhaustionSpec : ActaRuntimeTestBase<ConformanceFixture, StepExhaustionManifest>
{
    [Fact(DisplayName = "Step with large MaxAttempts exhausts after first failure when retry would exceed RetryWindow")]
    public async Task Step_exhausts_by_window_before_MaxAttempts_is_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "step-window-exhaust", JobPayload.None), ct);

        // One tick: step attempt 1 → body fails → CompleteStep checks window:
        // now()+30s > created_at+5s (30>5) → Exhausted, NOT retry-scheduled.
        // StepExhaustedException propagates → parent fails (MaxAttempts=1).
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Exhausted, step.Status);
        // attempt_number=1: far below MaxAttempts=100, proving window fired, not attempt-count.
        Assert.Equal(1, step.AttemptNumber);
        Assert.Null(step.NextRetryAtUtc);

        // Body ran exactly once (no retry attempt was made).
        Assert.Equal(1, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
    }

    [Fact(DisplayName = "Restarting a job whose step exhausted runs the step body again with a fresh budget")]
    public async Task Restart_runs_an_exhausted_step_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "step-window-exhaust", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Exhausted, Assert.Single(await ReadStepsAsync(enqueued.JobId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "provider is back", ct: ct)).Action);

        // Without the restart clearing the slot, this replay would throw on re-entry and the count stay at 1.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(2, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));
        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Exhausted, step.Status);
        Assert.Equal(1, step.AttemptNumber);
    }

    [Fact(DisplayName = "Re-entering an exhausted step slot throws StepExhaustedException without invoking the body")]
    public async Task Exhausted_slot_reentry_throws_without_invoking_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "step-exhaust-replay", JobPayload.None), ct);

        // Tick 1: step attempt 1 → body fails (invocations=1) → step Pending → parent re-arms (budget neutral).
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Pending, (await ReadStepsAsync(enqueued.JobId, ct)).Single().Status);
        Assert.Equal(1, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Tick 2: step attempt 2 → body fails (invocations=2) → step Exhausted →
        //   StepExhaustedException → the handler catches it and reschedules → Rearmed.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var stepAfterExhaust = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Exhausted, stepAfterExhaust.Status);
        Assert.Equal(2, stepAfterExhaust.AttemptNumber);
        Assert.Null(stepAfterExhaust.NextRetryAtUtc);
        // Body ran exactly twice: once per in-budget attempt.
        Assert.Equal(2, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Tick 3: the replay re-enters the exhausted slot → StartStep sees state=Exhausted → outcome=Exhausted(4) →
        //   StepExhaustedException WITHOUT body → uncaught this time, it fails the parent at once → RunOnceStatus.Failed.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body invocation count must NOT have increased: body was never called on re-entry.
        Assert.Equal(2, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Step row is still Exhausted, unchanged by the re-entry (StartStep is read-only on Exhausted slots).
        var stepAfterReentry = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepStatusCode.Exhausted, stepAfterReentry.Status);
        Assert.Equal(2, stepAfterReentry.AttemptNumber);
        Assert.Null(stepAfterReentry.NextRetryAtUtc);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        var finishedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(EventReasonCode.JobNonRetryableException, finishedEvent.ReasonCode);
        Assert.Equal(0, job.FailureCount);
    }

    private async Task<IReadOnlyList<StepEntity>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct);
    }
}
