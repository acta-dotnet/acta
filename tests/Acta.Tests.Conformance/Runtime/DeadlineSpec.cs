using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for whole-job deadlines. A Strict deadline auto-terminates an overdue job at
/// admission and refuses to re-arm a retry whose next attempt would land past the deadline, without
/// consuming the retry budget. An Advisory deadline never auto-terminates; the handler observes
/// ctx.IsOverdue and decides.
/// </summary>
[ConformanceSpec(
    "deadline.strict",
    "A Strict deadline terminates an overdue job and blocks a retry past the deadline",
    Area = "Execution",
    Contract = "A Strict deadline lands the job Cancelled with JobDeadlineExceeded at admission or when the next retry would overshoot, without consuming the retry budget.",
    Arrange = "Strict and Advisory deadline probes are registered with short whole-job deadlines.",
    Act = "An overdue Strict job runs, a Strict job retries past its deadline, and an overdue Advisory job runs its handler.",
    Assert = "The Strict jobs land Cancelled with JobDeadlineExceeded without consuming retry budget while the Advisory handler observes IsOverdue true."
)]
public abstract class DeadlineSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Strict admission cancels without running the handler")]
    public async Task Strict_overdue_at_admission_cancels_without_running_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "deadline-strict-probe", JobPayload.None), ct);

        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Cancelled, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = events.Where(e => e.EventCode == EventCode.JobExecutionFinished).OrderByDescending(e => e.Id).First();
        Assert.Equal(JobEventReasonCode.JobDeadlineExceeded, finished.JobEventReasonCode);

        var ran = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<IExecutionStore>(),
            enqueued.JobId,
            JobCheckpointKindCode.Variable,
            "ran",
            ct
        );
        Assert.Null(ran);
    }

    [Fact(DisplayName = "Strict blocks a retry past the deadline")]
    public async Task Strict_retry_past_deadline_is_terminal_not_rearmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "deadline-retry-probe", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Cancelled, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = events.Where(e => e.EventCode == EventCode.JobExecutionFinished).OrderByDescending(e => e.Id).First();
        Assert.Equal(JobEventReasonCode.JobDeadlineExceeded, finished.JobEventReasonCode);
    }

    [Fact(DisplayName = "Advisory never auto-terminates")]
    public async Task Advisory_runs_handler_even_when_overdue()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "deadline-advisory-probe", JobPayload.None), ct);

        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Succeeded, snapshot!.Status);
        Assert.Equal(0, snapshot.FailureCount);

        var overdue = await CheckpointSlot.GetAsync(
            Services.GetRequiredService<IExecutionStore>(),
            enqueued.JobId,
            JobCheckpointKindCode.Variable,
            "overdue",
            ct
        );
        Assert.NotNull(overdue);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.DoesNotContain(events, e => e.JobEventReasonCode == JobEventReasonCode.JobDeadlineExceeded);
    }
}
