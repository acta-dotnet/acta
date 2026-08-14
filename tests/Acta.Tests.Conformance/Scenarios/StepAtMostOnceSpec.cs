using System.Collections.Concurrent;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
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

    public static Task RunUncaughtAsync(JobContext ctx, CancellationToken ct) => ChargeAsync(ctx, ct);

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

    public static JobDescriptorManifest Descriptors { get; } =
        new([Probe(UncaughtProbe, AtMostOnceStepHandler.RunUncaughtAsync), Probe(CaughtProbe, AtMostOnceStepHandler.RunCaughtAsync)]);

    private static JobDescriptor Probe(string name, Func<JobContext, CancellationToken, Task> run) =>
        new(
            JobName: name,
            HandlerType: typeof(AtMostOnceStepHandler),
            MethodName: name,
            InputType: typeof(NoInput),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            InvocationKind: JobInvocationKind.Task,
            RequiresJobContextParameter: true,
            RequiresCancellationToken: true,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 5,
            AuditLevel: JobAuditLevelCode.Audit,
            AlertProfile: AlertProfileCode.OnFailure,
            Invoker: async (_, _, ctx, ct) =>
            {
                await run(ctx, ct);
                return new JobHandlerInvocationResult(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
        );
}

/// <summary>
/// Conformance for at-most-once steps (<c>ctx.RunStepAsync(..., o =&gt; o.AtMostOnce())</c>). The body
/// runs zero or one times, never twice: a first invocation still runs, but a step re-entered on replay
/// while still pending (a worker died after <c>start_step</c> recorded the pending row and before
/// <c>complete_step</c>) is not re-invoked. Instead the slot terminalizes <c>Interrupted</c> and
/// <c>RunStepAsync</c> throws <see cref="StepInterruptedException"/>, which is handler-owned policy:
/// uncaught it fails the parent terminally (no retry burn), caught it lets the parent proceed.
/// </summary>
[ConformanceSpec(
    "step.at-most-once",
    "At-most-once step re-entered before completion is interrupted",
    Area = "Steps",
    Contract = "AtMostOnce runs the body 0 or 1 times: a pending slot re-entered on replay terminalizes Interrupted and throws instead of re-invoking, version-idempotently.",
    Arrange = "A durable step slot is durably started (pending, never completed) to model a worker that died mid-flight.",
    Act = "The step is re-entered under AtMostOnce, both directly through start_step and through the runtime with the exception uncaught and caught.",
    Assert = "start_step returns Interrupted with no second version bump, the body never re-runs, an uncaught interruption fails the parent and a caught one lets it proceed."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartStepAsync))]
public abstract class StepAtMostOnceSpec<TFixture> : ActaRuntimeTestBase<TFixture, AtMostOnceStepManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string StepName = AtMostOnceStepHandler.StepName;

    [Fact(DisplayName = "A first invocation of an at-most-once step still runs the body (Invoke)")]
    public async Task First_invocation_still_invokes()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // No prior row: start_step inserts pending and returns Invoke even under at-most-once. The flag
        // only governs re-entry of an already-started slot, never the first legitimate run.
        var start = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: true, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, start.Outcome);
    }

    [Fact(DisplayName = "A pending step re-entered under at-most-once terminalizes Interrupted, version-idempotent")]
    public async Task Re_entry_terminalizes_interrupted_without_a_second_version_bump()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // Durably start the slot (pending) without completing it: models an attempt that recorded the
        // step start and then crashed.
        var staged = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, staged.Outcome);

        // First re-entry under at-most-once poisons the slot to Interrupted (one transition, one bump).
        var first = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: true, ct);
        Assert.Equal(StartStepOutcomeCode.Interrupted, first.Outcome);

        var row1 = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(JobStepStatusCode.Interrupted, row1.Status);
        var versionAfterInterrupt = row1.Version;

        // A later replay of an already-Interrupted slot returns Interrupted again with NO further mutation.
        var second = await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: true, ct);
        Assert.Equal(StartStepOutcomeCode.Interrupted, second.Outcome);

        var row2 = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(JobStepStatusCode.Interrupted, row2.Status);
        Assert.Equal(versionAfterInterrupt, row2.Version);
    }

    [Fact(DisplayName = "Uncaught StepInterruptedException fails the parent terminally without re-invoking the body")]
    public async Task Uncaught_interruption_fails_the_parent_and_explain_reports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, AtMostOnceStepManifest.UncaughtProbe, JobPayload.None),
            ct
        );

        // Stage a pending, never-completed step slot, then replay through the real runtime.
        await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: false, ct);
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // The body was never invoked on the replay.
        Assert.False(AtMostOnceStepHandler.BodyInvocations.ContainsKey(enqueued.JobId));

        // The slot is terminal Interrupted with the step-interrupted reason stamped by start_step.
        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(JobStepStatusCode.Interrupted, row.Status);
        Assert.Equal(JobEventReasonCode.JobStepInterrupted, row.ReasonCode);

        // Explain surfaces the interruption at both job and step level.
        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);
        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Failed, x!.Status);
        var step = Assert.Single(x.Steps);
        Assert.Equal(JobStepStatusCode.Interrupted, step.Status);
        Assert.Contains("reconcile", step.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Caught StepInterruptedException lets the parent proceed to Succeeded")]
    public async Task Caught_interruption_lets_the_parent_complete()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, AtMostOnceStepManifest.CaughtProbe, JobPayload.None),
            ct
        );

        await Services.GetRequiredService<IExecutionStore>().StartStepAsync(enqueued.JobId, StepName, atMostOnce: false, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body still never ran, but the handler reconciled and the job completed.
        Assert.False(AtMostOnceStepHandler.BodyInvocations.ContainsKey(enqueued.JobId));

        var row = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(JobStepStatusCode.Interrupted, row.Status);

        var status = await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct);
        Assert.Equal(JobStatusCode.Succeeded, status);
    }

    private async Task<JobStep> ReadStepAsync(long jobId, CancellationToken ct)
    {
        var steps = await Db.From<JobStep>().Where(s => s.JobId == jobId && s.Name == StepName).ToListAsync(ct);
        return Assert.Single(steps);
    }
}
