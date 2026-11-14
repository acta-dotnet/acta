using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>Handlers that catch the control flow a wait throws, and one that lets it through a finally.</summary>
internal static class SwallowedControlHandler
{
    public const string StepName = "check";

    public static async Task RunSwallowedWaitAsync(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.WaitSignalAsync("approval", ct);
        }
        catch (JobControlException)
        {
            // Swallowed: the handler goes on as if the wait had returned.
        }
    }

    public static Task RunSwallowedWaitInStepAsync(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            StepName,
            async token =>
            {
                try
                {
                    await ctx.WaitSignalAsync("approval", token);
                }
                catch (JobControlException)
                {
                    // Swallowed inside the body, which then returns.
                }
            },
            ct
        );

    public static async Task RunCleanupInFinallyAsync(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.WaitSignalAsync("approval", ct);
        }
        finally
        {
            await ctx.RunStepAsync(StepName, static _ => Task.CompletedTask, ct);
        }
    }
}

/// <summary>Spec-local manifest with the swallowing probes, isolated from TestJobsManifest.</summary>
public sealed class SwallowedControlManifest : IJobManifest
{
    public const string SwallowedWaitProbe = "swallowed-wait";
    public const string SwallowedWaitInStepProbe = "swallowed-wait-in-step";
    public const string CleanupInFinallyProbe = "cleanup-in-finally";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Probe(SwallowedWaitProbe, SwallowedControlHandler.RunSwallowedWaitAsync),
            Probe(SwallowedWaitInStepProbe, SwallowedControlHandler.RunSwallowedWaitInStepAsync),
            Probe(CleanupInFinallyProbe, SwallowedControlHandler.RunCleanupInFinallyAsync),
        ]);

    private static JobDescriptor Probe(string name, Func<JobContext, CancellationToken, Task> run) =>
        new(
            JobName: name,
            HandlerType: typeof(SwallowedControlHandler),
            MethodName: name,
            InputType: typeof(NoInput),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 5,
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
        );
}

/// <summary>
/// A wait, sleep or ctx control call ends the attempt by throwing a control-flow exception. A handler that catches it
/// and returns normally has run code the wait should have held back, so the attempt fails at once, with no retry, and
/// the message names the wait; a finally that runs as the wait unwinds is not a swallow.
/// </summary>
[ConformanceSpec(
    "handler.swallowed-control",
    "A handler that swallows its own control flow fails without a retry",
    Area = "Handlers",
    Contract = "A handler that returns after a wait or ctx control call threw fails with no retry, naming the call, and a finally running as it unwinds still suspends."
)]
public sealed class SwallowedControlSpec : ActaRuntimeTestBase<ConformanceFixture, SwallowedControlManifest>
{
    [Fact(DisplayName = "A handler that returns after swallowing a wait fails without a retry, naming the wait")]
    public async Task A_swallowed_wait_fails_the_job_without_a_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, SwallowedControlManifest.SwallowedWaitProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        var failed = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Contains("the wait for signal 'approval'", failed.EventMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A step body that swallows a wait is recorded, and the attempt fails at the handler's end")]
    public async Task A_wait_swallowed_in_a_step_body_fails_the_job_and_records_the_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, SwallowedControlManifest.SwallowedWaitInStepProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = Assert.Single(
            await Db.From<StepEntity>()
                .Where(s => s.JobId == enqueued.JobId && s.StepName == SwallowedControlHandler.StepName)
                .ToListAsync(ct)
        );
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A finally that runs a step as a wait unwinds still suspends the job")]
    public async Task A_cleanup_step_in_a_finally_around_a_wait_still_suspends()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, SwallowedControlManifest.CleanupInFinallyProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }
}
