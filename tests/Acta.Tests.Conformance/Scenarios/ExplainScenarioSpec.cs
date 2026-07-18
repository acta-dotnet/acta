using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for <c>IJobs.ExplainAsync</c> over live states driven through the real runtime: a
/// signal wait lands the job Suspended and the explanation names the pending signal; a raise drives it
/// to Done and the explanation reports terminal success. Covers the full facade composition (resolve +
/// GetJobExplanation + clock + JobExplainer), beyond the point-read column mapping.
/// </summary>
[ConformanceSpec(
    "explain.live-states",
    "Explain reports live Suspended and Done states through the facade",
    Area = "Reads",
    Contract = "ExplainAsync reports a signal-suspended job as Suspended awaiting its signal and a finished job as Done.",
    Arrange = "A job-wait-signal handler is enqueued and driven through the real runtime loop.",
    Act = "ExplainAsync is called after the wait suspends the job and again after a raise drives it to completion.",
    Assert = "The suspended read names the pending signal wait and the completed read reports Done."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobExplanationAsync))]
public abstract class ExplainScenarioSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Explain reports a signal-suspended job as Suspended awaiting its signal")]
    public async Task Explains_a_suspended_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);

        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Suspended, x!.Status);
        Assert.NotNull(x.ActiveWait);
        Assert.Equal(JobExplainWaitKind.Signal, x.ActiveWait!.Kind);
        Assert.Equal("go", x.ActiveWait.Name);
        Assert.Null(x.Lease);
        Assert.Contains(x.NextActions, a => a.Kind == "raise-signal");
        Assert.Contains(x.NextActions, a => a.Kind == "cancel");
    }

    [Fact(DisplayName = "Explain reports a released-and-finished job as Done")]
    public async Task Explains_a_completed_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobControlAction.Applied, (await Jobs.RaiseSignalAsync(JobLookup.ById(enqueued.JobId), "go", ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);

        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Done, x!.Status);
        Assert.Equal("Done.", x.Headline);
        Assert.Null(x.ActiveWait);
    }

    [Fact(DisplayName = "Explain reports a completed durable step as non-rerunning")]
    public async Task Explains_a_completed_durable_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);

        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Done, x!.Status);
        var step = Assert.Single(x.Steps);
        Assert.Equal("compute", step.Name);
        Assert.Equal(JobStepStateCode.Succeeded, step.State);
        Assert.Contains("will not rerun", step.Explanation);
    }
}
