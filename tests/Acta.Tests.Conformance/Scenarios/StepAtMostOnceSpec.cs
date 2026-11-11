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

    public static Task RunThrowsRescheduleAsync(JobContext ctx, CancellationToken ct) =>
        ThrowInsideAsync(ctx, new RescheduleJobException(TimeSpan.FromMinutes(1)), ct);

    public static Task RunThrowsPauseAsync(JobContext ctx, CancellationToken ct) =>
        ThrowInsideAsync(ctx, new HandlerPauseException("hold"), ct);

    private static Task ThrowInsideAsync(JobContext ctx, JobControlException control, CancellationToken ct) =>
        ctx.RunStepAsync(
            StepName,
            async _ =>
            {
                await Task.CompletedTask;
                throw control;
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

/// <summary>Spec-local manifest with the two at-most-once probes, isolated from TestJobsManifest.</summary>
public sealed class AtMostOnceStepManifest : IJobManifest
{
    public const string UncaughtProbe = "at-most-once-uncaught";
    public const string CaughtProbe = "at-most-once-caught";
    public const string WaitInsideProbe = "at-most-once-wait-inside";
    public const string ThrowsRescheduleProbe = "at-most-once-throws-reschedule";
    public const string ThrowsPauseProbe = "at-most-once-throws-pause";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Probe(UncaughtProbe, AtMostOnceStepHandler.RunUncaughtAsync),
            Probe(CaughtProbe, AtMostOnceStepHandler.RunCaughtAsync),
            Probe(WaitInsideProbe, AtMostOnceStepHandler.RunWaitInsideAsync),
            Probe(ThrowsRescheduleProbe, AtMostOnceStepHandler.RunThrowsRescheduleAsync),
            Probe(ThrowsPauseProbe, AtMostOnceStepHandler.RunThrowsPauseAsync),
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
        Assert.Equal(StepStatusCode.Exhausted, row.Status);
        Assert.Equal(refused.Message, row.FailureMessage);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A RescheduleJobException an at-most-once body throws is refused and exhausts the step")]
    public Task Thrown_reschedule_inside_an_at_most_once_body_is_refused() =>
        AssertThrownSuspensionRefusedAsync(AtMostOnceStepManifest.ThrowsRescheduleProbe, nameof(RescheduleJobException));

    [Fact(DisplayName = "A HandlerPauseException an at-most-once body throws is refused and exhausts the step")]
    public Task Thrown_pause_inside_an_at_most_once_body_is_refused() =>
        AssertThrownSuspensionRefusedAsync(AtMostOnceStepManifest.ThrowsPauseProbe, nameof(HandlerPauseException));

    // Uncaught, the suspension would re-arm the job and leave the step started, so the next attempt would interrupt it.
    private async Task AssertThrownSuspensionRefusedAsync(string probe, string thrown)
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, probe, JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(StepStatusCode.Exhausted, row.Status);
        Assert.StartsWith(
            $"A thrown {thrown} inside the AtMostOnce step '{StepName}' is refused",
            row.FailureMessage,
            StringComparison.Ordinal
        );
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
