using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Facade ControlBatchAsync: loops over the single-job control verbs, positionally aligning the
/// returned outcomes with the caller's targets. No dedicated SQL routine backs this - it dispatches to
/// the already-proven single-job store methods (see the rationale comment on the implementation).
/// </summary>
[ConformanceSpec(
    "job.control-batch",
    "ControlBatchAsync applies one verb to many jobs with positional outcomes.",
    Area = "Control",
    Contract = "ControlBatchAsync loops the single-job verb over every target, aligning outcomes to input order, validating required options first.",
    Arrange = "A Ready job, a completed job, and an unknown job id.",
    Act = "ControlBatchAsync(Cancel, [ready, done, unknown]) is invoked once.",
    Assert = "Results are [Applied, Rejected, NotFound] positionally and the ready job is durably cancelled."
)]
public abstract class JobControlBatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "ControlBatchAsync cancels positionally: applied, rejected, notFound")]
    public async Task Batch_cancel_returns_positional_outcomes()
    {
        var ct = TestContext.Current.CancellationToken;

        var ready = await EnqueueAsync(ct);
        var done = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct)).JobId;

        var results = await Jobs.ControlBatchAsync(
            JobBatchAction.Cancel,
            [JobLookup.ById(ready), JobLookup.ById(done), JobLookup.ById(999_999_999_999L)],
            actorKey: "spec-actor",
            ct: ct
        );

        Assert.Equal(3, results.Count);
        Assert.Equal(JobControlAction.Applied, results[0].Action);
        Assert.Equal(JobControlAction.Rejected, results[1].Action);
        Assert.Equal(JobControlAction.NotFound, results[2].Action);

        var snapshot = await Jobs.GetAsync(JobLookup.ById(ready), ct);
        Assert.NotNull(snapshot);
        Assert.Equal(JobStatusCode.Cancelled, snapshot!.Status);
    }

    [Fact(DisplayName = "ControlBatchAsync reschedule without NextRunAtUtc throws before touching any target")]
    public async Task Batch_reschedule_without_next_run_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var ready = await EnqueueAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Jobs.ControlBatchAsync(JobBatchAction.Reschedule, [JobLookup.ById(ready)], ct: ct).AsTask()
        );

        var snapshot = await Jobs.GetAsync(JobLookup.ById(ready), ct);
        Assert.Equal(JobStatusCode.Ready, snapshot!.Status);
    }

    [Fact(DisplayName = "ControlBatchAsync rejects a batch over the 1000-target cap")]
    public async Task Batch_over_cap_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = Enumerable.Range(1, 1001).Select(i => JobLookup.ById((long)i)).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => Jobs.ControlBatchAsync(JobBatchAction.Cancel, targets, ct: ct).AsTask());
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }
}
