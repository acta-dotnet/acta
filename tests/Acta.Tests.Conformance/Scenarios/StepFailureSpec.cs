using System.Collections.Concurrent;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Spec-local body-invocation counter: keyed by job id so parallel tests don't collide.
/// </summary>
internal static class StepFailureProbes
{
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    private static readonly ConcurrentDictionary<long, int> Failures = new();

    public static int RecordInvocation(long jobId) => BodyInvocations.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    // An at-most-once body that always fails: its first throw fails the step.
    public static Task Fail(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "always-fails",
            async bodyCt =>
            {
                RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                throw new InvalidOperationException("step body always fails");
            },
            o => o.AtMostOnce(),
            ct
        );

    // The handler catches the first failure and reschedules, so the replay re-enters the failed step; the second
    // failure goes uncaught.
    public static async Task FailReplay(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await Fail(ctx, ct);
        }
        catch (StepFailedException) when (Failures.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1) == 1)
        {
            await ctx.RescheduleAsync(TimeSpan.Zero, ct: ct);
        }
    }
}

/// <summary>
/// Hand-written manifest for the two failure-probe jobs. Kept isolated from
/// <c>TestJobsManifest</c> so the shared step counters and job definitions are unaffected.
/// </summary>
public sealed class StepFailureManifest : IJobManifest
{
    public static JobDescriptorManifest Descriptors { get; } =
        new([Probe("step-fail-replay", StepFailureProbes.FailReplay), Probe("step-fail-restart", StepFailureProbes.Fail)]);

    private static JobDescriptor Probe(string name, Func<JobContext, CancellationToken, Task> run) =>
        new(
            JobName: name,
            HandlerType: typeof(StepFailureProbes),
            MethodName: name,
            InputType: typeof(NoInput),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 2,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: async (_, _, ctx, ct) =>
            {
                await run(ctx, ct);
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeResult: null
        )
        {
            Backoff = "0s",
        };
}

/// <summary>
/// Conformance for a failed step, an at-most-once step whose body threw: re-entering it throws without running the
/// body, and an operator restart clears it, so the body runs again.
/// </summary>
[ConformanceSpec(
    "step.failure",
    "A failed step re-throws on re-entry until a restart clears it",
    Area = "Steps",
    Contract = "Re-entering a failed at-most-once step throws StepFailedException without running the body, and a restart runs it afresh."
)]
public sealed class StepFailureSpec : ActaRuntimeTestBase<ConformanceFixture, StepFailureManifest>
{
    [Fact(DisplayName = "Restarting a job whose step failed runs the step body again")]
    public async Task Restart_runs_a_failed_step_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "step-fail-restart", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Failed, Assert.Single(await ReadStepsAsync(enqueued.JobId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "provider is back", ct: ct)).Action);

        // Without the restart clearing the step, this replay would throw on re-entry and the count stay at 1.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(2, StepFailureProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));
        Assert.Equal(StepStatusCode.Failed, Assert.Single(await ReadStepsAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "Re-entering a failed step throws StepFailedException without invoking the body")]
    public async Task Failed_step_reentry_throws_without_invoking_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "step-fail-replay", JobPayload.None), ct);

        // Tick 1: the body fails, the step is Failed, and the handler catches StepFailedException and reschedules.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Failed, Assert.Single(await ReadStepsAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, StepFailureProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));

        // Tick 2: the replay re-enters the failed step, which throws without running the body; uncaught this time,
        // it fails the parent at once.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, StepFailureProbes.BodyInvocations.GetValueOrDefault(enqueued.JobId));
        Assert.Equal(StepStatusCode.Failed, Assert.Single(await ReadStepsAsync(enqueued.JobId, ct)).Status);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        var failedEvent = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failedEvent, "reason"));
        Assert.Equal(0, job.FailureCount);
    }

    private async Task<IReadOnlyList<StepEntity>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct);
    }
}
