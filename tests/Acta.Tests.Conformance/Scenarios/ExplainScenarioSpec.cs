using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for <c>IJobs.ExplainAsync</c> over live states driven through the real runtime: a
/// signal wait lands the job Suspended and the explanation names the pending signal, a bounded child
/// wait names the awaited child and its deadline, and a raise drives the job to Succeeded. Covers the
/// full facade composition (resolve + GetJobExplanation + clock + JobExplainer), beyond the point-read
/// column mapping.
/// </summary>
[ConformanceSpec(
    "explain.live-states",
    "Explain reports live Suspended and Succeeded states through the facade",
    Area = "Reads",
    Contract = "ExplainAsync reports a suspended job as awaiting its signal or its child, and a finished job as Succeeded.",
    Arrange = "Signal-waiting, child-waiting and step handlers are enqueued and driven through the real runtime loop.",
    Act = "ExplainAsync is called while a job is suspended on a wait and again after it runs to completion.",
    Assert = "The suspended reads name the pending signal or child wait and its deadline, and the completed read reports Succeeded."
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
        Assert.Equal(JobCheckpointKindCode.Signal, x.ActiveWait!.Kind);
        Assert.Equal("go", x.ActiveWait.Name);
        Assert.Null(x.Lease);
        Assert.Contains(x.NextActions, a => a.Kind == "raise-signal");
        Assert.Contains(x.NextActions, a => a.Kind == "cancel");
    }

    [Fact(DisplayName = "Explain reports a released-and-finished job as Succeeded")]
    public async Task Explains_a_completed_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(
            ControlAction.Applied,
            (await Jobs.RaiseSignalAsync(JobLookup.ById(enqueued.JobId), "go", JobPayload.None, ct: ct)).Action
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);

        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Succeeded, x!.Status);
        Assert.Equal("Succeeded.", x.Headline);
        Assert.Null(x.ActiveWait);
    }

    [Fact(DisplayName = "Explain reports a child-suspended parent as awaiting that child, with its deadline")]
    public async Task Explains_a_parent_suspended_on_a_bounded_child_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "job-parent-try-wait-child",
                JobPayload.Json(new TestJobs.TryWaitChildStart("job-wait-signal"))
            ),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<Acta.Relational.Entities.Job>().Where(j => j.ParentId == parent.JobId).ToListAsync(ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(parent.JobId), ct);

        // The live path, not just the pure function: this also proves the ChildLatch rows reach the
        // explanation projection at all, which no signal-only fact could show.
        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Suspended, x!.Status);
        Assert.Equal(JobCheckpointKindCode.ChildLatch, x.ActiveWait!.Kind);
        Assert.Equal($"sys.child.{child.Id}", x.ActiveWait.Name);
        Assert.NotNull(x.ActiveWait.DueAtUtc);
        Assert.Contains($"waiting for child job {child.Id}", x.Headline, StringComparison.Ordinal);
        Assert.Contains("times out at", x.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain(x.NextActions, a => a.Kind == "raise-signal");
        Assert.Contains(x.NextActions, a => a.Kind == "cancel");
    }

    [Fact(DisplayName = "Explain reports a completed durable step as non-rerunning")]
    public async Task Explains_a_completed_durable_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var x = await Jobs.ExplainAsync(JobLookup.ById(enqueued.JobId), ct);

        Assert.NotNull(x);
        Assert.Equal(JobStatusCode.Succeeded, x!.Status);
        var step = Assert.Single(x.Steps);
        Assert.Equal("compute", step.Name);
        Assert.Equal(JobStepStatusCode.Succeeded, step.Status);
        Assert.Contains("will not rerun", step.Explanation);
    }
}
