using System.Collections.Concurrent;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Handlers for the at-most-once step probes. The body records its invocation per job id so a spec can
/// assert it was never run on the interrupted replay. One handler lets
/// <see cref="StepInterruptedException"/> propagate (uncaught -> parent Failed); the other catches it and
/// continues (caught -> parent proceeds), modelling handler-owned reconciliation policy.
/// </summary>
internal static class AtMostOnceStepHandler
{
    public const string StepName = "charge";

    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    private static Task ChargeAsync(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            StepName,
            _ =>
            {
                BodyInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
                return Task.CompletedTask;
            },
            o => o.AtMostOnce(),
            ct
        );

    /// <summary>What the wait inside an at-most-once body threw at the call, per job id.</summary>
    public static readonly ConcurrentDictionary<long, Exception> RefusedWaits = new();

    public static Task RunUncaughtAsync(JobContext ctx, CancellationToken ct) => ChargeAsync(ctx, ct);

    /// <summary>How often the checkout step around the unknown charge ran its body, per job id.</summary>
    public static readonly ConcurrentDictionary<long, int> CheckoutInvocations = new();

    /// <summary>An at-most-once checkout whose body runs the charge whose outcome is unknown.</summary>
    public static Task RunNestedOutcomeUnknownAsync(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            "checkout",
            token =>
            {
                CheckoutInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
                return RunOutcomeUnknownAsync(ctx, token);
            },
            o => o.AtMostOnce(),
            ct
        );

    /// <summary>A charge whose call timed out after the request left, so the body cannot tell whether it went through.</summary>
    public static Task RunOutcomeUnknownAsync(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            StepName,
            _ =>
            {
                BodyInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
                throw new OutcomeUnknownException("The capture timed out after the request left.", new TimeoutException());
            },
            o => o.AtMostOnce(),
            ct
        );

    public static Task RunWaitInsideAsync(JobContext ctx, CancellationToken ct) =>
        ctx.RunStepAsync(
            StepName,
            async token =>
            {
                try
                {
                    await ctx.WaitSignalAsync("approval", token);
                }
                catch (Exception ex)
                {
                    RefusedWaits[ctx.JobId] = ex;
                    throw;
                }
            },
            o => o.AtMostOnce(),
            ct
        );

    // A wait begun outside the body whose control flow the body ends with, as a wait started before the step and awaited
    // inside it does.
    public static async Task RunCarriedWaitAsync(JobContext ctx, CancellationToken ct)
    {
        JobControlException? carried = null;
        try
        {
            await ctx.WaitSignalAsync("approval", ct);
        }
        catch (JobControlException ex)
        {
            carried = ex;
        }

        await ctx.RunStepAsync(StepName, _ => Task.FromException(carried!), o => o.AtMostOnce(), ct);
    }

    public static async Task RunCaughtAsync(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ChargeAsync(ctx, ct);
        }
        catch (StepInterruptedException)
        {
            // Handler-owned policy: reconcile externally (elided) and continue. The job still completes.
        }
    }
}

/// <summary>Spec-local manifest with the at-most-once probes, isolated from TestJobsManifest.</summary>
public sealed class AtMostOnceStepManifest : IJobManifest
{
    public const string UncaughtProbe = "at-most-once-uncaught";
    public const string CaughtProbe = "at-most-once-caught";
    public const string WaitInsideProbe = "at-most-once-wait-inside";
    public const string CarriedWaitProbe = "at-most-once-carried-wait";
    public const string OutcomeUnknownProbe = "at-most-once-outcome-unknown";
    public const string NestedOutcomeUnknownProbe = "at-most-once-nested-outcome-unknown";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Probe(UncaughtProbe, AtMostOnceStepHandler.RunUncaughtAsync),
            Probe(OutcomeUnknownProbe, AtMostOnceStepHandler.RunOutcomeUnknownAsync),
            Probe(NestedOutcomeUnknownProbe, AtMostOnceStepHandler.RunNestedOutcomeUnknownAsync),
            Probe(CaughtProbe, AtMostOnceStepHandler.RunCaughtAsync),
            Probe(WaitInsideProbe, AtMostOnceStepHandler.RunWaitInsideAsync),
            Probe(CarriedWaitProbe, AtMostOnceStepHandler.RunCarriedWaitAsync),
        ]);

    private static JobDescriptor Probe(string name, Func<JobContext, CancellationToken, Task> run) =>
        new(
            JobName: name,
            HandlerType: typeof(AtMostOnceStepHandler),
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
/// Conformance for at-most-once steps (<c>ctx.RunStepAsync(..., o =&gt; o.AtMostOnce())</c>). The body
/// runs zero or one times, never twice: a first invocation still runs, but a step re-entered on replay
/// while still pending (a worker died after <c>start_job_step</c> recorded the pending row and before
/// <c>complete_job_step</c>) is not re-invoked. Instead the step terminalizes <c>Interrupted</c> and
/// <c>RunStepAsync</c> throws <see cref="StepInterruptedException"/>, which is handler-owned policy:
/// uncaught it fails the parent terminally (no retry burn), caught it lets the parent proceed. A wait
/// inside the body is refused at the call, since its resume would replay the body.
/// </summary>
[ConformanceSpec(
    "step.at-most-once",
    "At-most-once step re-entered before completion is interrupted",
    Area = "Steps",
    Contract = "AtMostOnce runs the body 0 or 1 times: a re-entered pending step terminalizes Interrupted, and a wait inside the body is refused before it arms anything."
)]
public sealed class StepAtMostOnceSpec : ActaRuntimeTestBase<ConformanceFixture, AtMostOnceStepManifest>
{
    private const string StepName = AtMostOnceStepHandler.StepName;

    [Fact(DisplayName = "A first invocation of an at-most-once step still runs the body (Invoke)")]
    public async Task First_invocation_still_invokes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // No prior row: start_job_step inserts pending and returns Invoke even under at-most-once. The flag
        // only governs re-entry of an already-started step, never the first legitimate run.
        var start = await Services
            .GetRequiredService<ExecutionOperations>()
            .StartJobStepAsync(await ExecutingAsync(enqueued, ct), StepName, atMostOnce: true, ct);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, start.Outcome);
    }

    [Fact(DisplayName = "A pending step re-entered under at-most-once terminalizes Interrupted, version-idempotent")]
    public async Task Re_entry_terminalizes_interrupted_without_a_second_version_bump()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // Durably start the step (pending) without completing it: models an attempt that recorded the
        // step start and then crashed.
        var job = await ExecutingAsync(enqueued, ct);
        var staged = await Services.GetRequiredService<ExecutionOperations>().StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, staged.Outcome);

        // First re-entry under at-most-once poisons the step to Interrupted (one transition, one bump).
        var first = await Services.GetRequiredService<ExecutionOperations>().StartJobStepAsync(job, StepName, atMostOnce: true, ct);
        Assert.Equal(StartJobStepOutcomeCode.Interrupted, first.Outcome);

        var row1 = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Interrupted, row1.Status);
        var versionAfterInterrupt = row1.Version;

        // A later replay of an already-Interrupted step returns Interrupted again with NO further mutation.
        var second = await Services.GetRequiredService<ExecutionOperations>().StartJobStepAsync(job, StepName, atMostOnce: true, ct);
        Assert.Equal(StartJobStepOutcomeCode.Interrupted, second.Outcome);

        var row2 = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Interrupted, row2.Status);
        Assert.Equal(versionAfterInterrupt, row2.Version);
    }

    [Fact(DisplayName = "Uncaught StepInterruptedException fails the parent terminally without re-invoking the body")]
    public async Task Uncaught_interruption_fails_the_parent_and_explain_reports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // Stage a pending, never-completed step, then replay through the real runtime.
        await StartedThenLostAsync(enqueued, ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // The body was never invoked on the replay.
        Assert.False(AtMostOnceStepHandler.BodyInvocations.ContainsKey(enqueued.JobId));

        // start_job_step stamped the step terminal Interrupted.
        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Interrupted, row.Status);

        // Explain surfaces the interruption at both job and step level.
        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);
        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Failed, x!.Status);
        var step = Assert.Single(x.Steps);
        Assert.Equal(StepStatusCode.Interrupted, step.Status);
        Assert.Contains("reconcile", step.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "A body that throws OutcomeUnknownException ends the step as a crash does, and a restart does not run it again")]
    public async Task Outcome_unknown_ends_the_step_as_a_crash_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.OutcomeUnknownProbe, JobPayload.None),
            ct
        );

        // The failure names the cause and fails the job at once, with no retry.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);
        Assert.Equal(JobStatusCode.Failed, x!.Status);
        Assert.Contains("timed out after the request left", x.Reason);

        // A restart re-enters the step, which interrupts it, and the body stays at its one run.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "provider answered", ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, AtMostOnceStepHandler.BodyInvocations[enqueued.JobId]);
        Assert.Equal(StepStatusCode.Interrupted, (await ReadStepAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "An at-most-once step around one whose outcome is unknown stays started too, and a restart runs neither body")]
    public async Task Outcome_unknown_inside_an_at_most_once_step_leaves_both_started()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.NestedOutcomeUnknownProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "provider answered", ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, AtMostOnceStepHandler.CheckoutInvocations[enqueued.JobId]);
        Assert.Equal(1, AtMostOnceStepHandler.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "Caught StepInterruptedException lets the parent proceed to Succeeded")]
    public async Task Caught_interruption_lets_the_parent_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.CaughtProbe, JobPayload.None),
            ct
        );

        await StartedThenLostAsync(enqueued, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body still never ran, but the handler reconciled and the job completed.
        Assert.False(AtMostOnceStepHandler.BodyInvocations.ContainsKey(enqueued.JobId));

        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Interrupted, row.Status);

        var status = (await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct))?.Status;
        Assert.Equal(JobStatusCode.Succeeded, status);
    }

    [Fact(DisplayName = "A wait inside an at-most-once body is refused at the call and arms nothing")]
    public async Task Wait_inside_an_at_most_once_body_is_refused_at_the_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.WaitInsideProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var refused = Assert.IsType<InvalidOperationException>(AtMostOnceStepHandler.RefusedWaits[enqueued.JobId]);
        Assert.Contains("AtMostOnce", refused.Message, StringComparison.Ordinal);
        Assert.Contains("out of the step", refused.Message, StringComparison.Ordinal);

        // The wait armed no checkpoint, and the body's failure is the step's terminal one, so it never runs again.
        Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Failed, row.Status);
        Assert.Equal(refused.Message, row.FailureMessage);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A wait begun outside an at-most-once body and ended in it is refused and fails the step")]
    public async Task A_wait_carried_into_an_at_most_once_body_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, AtMostOnceStepManifest.CarriedWaitProbe, JobPayload.None),
            ct
        );

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Failed, row.Status);
        Assert.Contains($"inside the AtMostOnce step '{StepName}' is refused", row.FailureMessage, StringComparison.Ordinal);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    // The job staged Executing, as a claim leaves it, so the step writes pass the execution fence.
    private Task<ExecutionFence> ExecutingAsync(EnqueueJobResponse enqueued, CancellationToken ct) =>
        ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);

    // An attempt that started the step and was lost before completing it, leaving the job Ready for the replay.
    private async Task StartedThenLostAsync(EnqueueJobResponse enqueued, CancellationToken ct)
    {
        var lost = await ExecutingAsync(enqueued, ct);
        await Services.GetRequiredService<ExecutionOperations>().StartJobStepAsync(lost, StepName, atMostOnce: false, ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, enqueued.JobId, ct);
    }

    private async Task<StepEntity> ReadStepAsync(long jobId, CancellationToken ct)
    {
        var steps = await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct);
        return Assert.Single(steps);
    }
}
