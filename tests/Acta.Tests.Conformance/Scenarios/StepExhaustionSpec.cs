using System.Collections.Concurrent;
using System.Collections.Immutable;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

// ---------- spec-local probe ----------

/// <summary>
/// Spec-local body-invocation counter: keyed by job id so parallel tests don't collide.
/// </summary>
internal static class StepExhaustionProbes
{
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    public static int RecordInvocation(long jobId) => BodyInvocations.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    // Job 1: parent MaxAttempts=2, zero-backoff; step MaxAttempts=2, zero-backoff, always fails.
    // Proves: exhausted slot re-entry throws WITHOUT re-running the body.
    public static async Task ExhaustReplay(JobContext ctx, CancellationToken ct) =>
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

// ---------- spec-local manifest ----------

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
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: JobAlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await StepExhaustionProbes.ExhaustReplay(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
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
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 1,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: JobAlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await StepExhaustionProbes.WindowExhaust(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            ),
        ]);
}

// ---------- spec ----------

/// <summary>
/// Conformance for step exhaustion guarantees beyond attempt-count: window exhaustion fires
/// before MaxAttempts is reached, and re-entering an exhausted slot throws without running the body.
/// </summary>
[ConformanceSpec(
    "step.exhaustion",
    "Step exhausts by retry-window and re-entry replays without body invocation",
    Area = "Steps",
    Contract = "A step exhausts when a retry would exceed its window before MaxAttempts is reached, and re-entering an exhausted slot throws without running the body.",
    Arrange = "One always-failing step has MaxAttempts 2 with zero backoff and another has MaxAttempts 100 with a 5s RetryWindow and 30s backoff.",
    Act = "Each parent runs until its step exhausts and a replayed handler re-enters the exhausted slot.",
    Assert = "The windowed step exhausts after one failure far below MaxAttempts, and re-entry throws StepExhaustedException without running the body."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartStepAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteStepAsync))]
public abstract class StepExhaustionSpec<TFixture> : ActaRuntimeTestBase<TFixture, StepExhaustionManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Step with large MaxAttempts exhausts after first failure when retry would exceed RetryWindow")]
    public async Task Step_exhausts_by_window_before_MaxAttempts_is_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "step-window-exhaust", JobPayload.None), ct);

        // One tick: step attempt 1 → body fails → CompleteStep checks window:
        // now()+30s > created_at+5s (30>5) → Exhausted, NOT retry-scheduled.
        // StepExhaustedException propagates → parent fails (MaxAttempts=1).
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStateCode.Exhausted, step.State);
        // attempt_number=1: far below MaxAttempts=100, proving window fired, not attempt-count.
        Assert.Equal((short)1, step.AttemptNumber);
        Assert.Null(step.NextRetryAtUtc);

        // Body ran exactly once (no retry attempt was made).
        Assert.Equal(1, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
    }

    [Fact(DisplayName = "Re-entering an exhausted step slot throws StepExhaustedException without invoking the body")]
    public async Task Exhausted_slot_reentry_throws_without_invoking_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "step-exhaust-replay", JobPayload.None), ct);

        // Tick 1: step attempt 1 → body fails (invocations=1) → step Pending → parent re-arms (budget neutral).
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStepStateCode.Pending, (await ReadStepsAsync(enqueued.JobId, ct)).Single().State);
        Assert.Equal(1, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Tick 2: step attempt 2 → body fails (invocations=2) → step Exhausted →
        //   StepExhaustedException → parent attempt 1 fails (FailureCount=1 of MaxAttempts=2) → Rearmed.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var stepAfterExhaust = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStateCode.Exhausted, stepAfterExhaust.State);
        Assert.Equal((short)2, stepAfterExhaust.AttemptNumber);
        Assert.Null(stepAfterExhaust.NextRetryAtUtc);
        // Body ran exactly twice: once per in-budget attempt.
        Assert.Equal(2, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Make the parent immediately claimable regardless of backoff jitter.
        await SetJobNextRunAsync(Db, enqueued.JobId, DateTime.UtcNow.AddMinutes(-1), ct);

        // Tick 3: parent attempt 2 replays the handler → RunStepAsync re-enters the exhausted slot →
        //   StartStep sees state=Exhausted → outcome=Exhausted(4) → StepExhaustedException WITHOUT body →
        //   parent attempt 2 fails (FailureCount=2, MaxAttempts=2) → RunOnceOutcome.Failed.
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body invocation count must NOT have increased: body was never called on re-entry.
        Assert.Equal(2, StepExhaustionProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Step row is still Exhausted, unchanged by the re-entry (StartStep is read-only on Exhausted slots).
        var stepAfterReentry = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStateCode.Exhausted, stepAfterReentry.State);
        Assert.Equal((short)2, stepAfterReentry.AttemptNumber);
        Assert.Null(stepAfterReentry.NextRetryAtUtc);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        var finishedEvent = await ReadLatestEventAsync(enqueued.JobId, JobEventCode.JobExecutionFinished, ct);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, finishedEvent.ReasonCode);
    }

    // ---------- helpers ----------

    private async Task<IReadOnlyList<JobStep>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<JobStep>().Where(s => s.JobId == jobId).ToListAsync(ct);
    }

    private static Task SetJobNextRunAsync(IDbSession db, long jobId, DateTime nextRun, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", nextRun),
            ("@p_id", jobId)
        );
}
