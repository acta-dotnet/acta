using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for "null stores nothing" in a step: a body that returns null, or a value whose JSON is null, records the
/// step done with no result, and a replay returns null without running the body again.
/// </summary>
[ConformanceSpec(
    "steps.null-stores-nothing",
    "A step that returns null is done with no result and replays as null",
    Area = "Steps",
    Contract = "A step whose body returns null records no result and replays as null without its body, which a non-nullable value type refuses."
)]
[Deploys("job-step-json-null", "job-step-null-at-most-once", "job-step-null-replay", "job-step-null-reset", "job-step-null-value-type")]
public sealed class NullStepSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<NullStepSpec>>
{
    [Fact(DisplayName = "A step that returned null records no result and replays as null without running its body again")]
    public async Task A_null_step_replays_as_null_without_its_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-null-replay", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.True(await Jobs.GetResultAsync<bool>(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        await AssertDoneWithNoResultAsync(enqueued.JobId, ct);
        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<string>(enqueued, "lookup", ct));
    }

    [Fact(DisplayName = "A step whose result's JSON is null records no result and replays as null")]
    public async Task A_step_whose_json_is_null_records_no_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-json-null", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await AssertDoneWithNoResultAsync(enqueued.JobId, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.True(await Jobs.GetResultAsync<bool>(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "An at-most-once step that returned null is done, and a restart replays it rather than finding it interrupted")]
    public async Task An_at_most_once_null_step_is_done_and_replays_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-null-at-most-once", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        await AssertDoneWithNoResultAsync(enqueued.JobId, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.True(await Jobs.GetResultAsync<bool>(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        await AssertDoneWithNoResultAsync(enqueued.JobId, ct);
    }

    [Fact(DisplayName = "A null step replays as null after a restart, and runs again after the job resets its state")]
    public async Task A_null_step_after_a_restart_and_after_a_reset()
    {
        var ct = TestContext.Current.CancellationToken;
        var kept = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-step-null-reset", JobPayload.Json(new NullStepReset(false))),
            ct
        );
        var reset = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-step-null-reset", JobPayload.Json(new NullStepReset(true))),
            ct
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(kept, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(reset, ct));
        Assert.Empty(await Db.From<StepEntity>().Where(s => s.JobId == reset.JobId).ToListAsync(ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(kept, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(reset, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(kept, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(reset, ct));

        // A restart keeps the done step, which replays as null; a reset cleared it, so its body ran again.
        Assert.Equal(1, JobStepProbes.BodyInvocations[kept.JobId]);
        Assert.Equal(2, JobStepProbes.BodyInvocations[reset.JobId]);
        Assert.True(await Jobs.GetResultAsync<bool>(kept, ct));
        await AssertDoneWithNoResultAsync(kept.JobId, ct);
    }

    [Fact(
        DisplayName = "A step that returned null and is replayed as a non-nullable value type fails the job for good as a contract mismatch"
    )]
    public async Task A_null_step_read_as_a_non_nullable_value_type_is_a_contract_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-null-value-type", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(0, job.FailureCount);
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Equal(nameof(StepResultContractMismatchException), await Jobs.GetVariableOrDefaultAsync<string>(enqueued, "ended-by", ct));

        // The read by lookup answers the type's default for a step that recorded no result.
        Assert.Equal(0, await Jobs.GetStepResultOrDefaultAsync<int>(enqueued, "count", ct));
        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<int?>(enqueued, "count", ct));
    }

    /// <summary>The job's one step is Succeeded with no result: format 0 and a SQL null, never an empty value.</summary>
    private async Task AssertDoneWithNoResultAsync(long jobId, CancellationToken ct)
    {
        var step = Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct));
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(0, step.ResultFormatId);
        Assert.Null(step.Result);
    }
}
