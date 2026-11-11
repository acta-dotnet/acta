using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>start_job_step</c>, which opens or reads a durable step and decides what the step does on this entry.</summary>
[ConformanceSpec(
    "start-job-step.decision",
    "Starting a step invokes, replays, or refuses from the step's state",
    Area = "Execution",
    Contract = "A step start invokes a new or unfinished ordinary step, replays a success, interrupts a re-entered at-most-once step, and refuses a terminal failure."
)]
public sealed class StartJobStepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string StepName = "charge";

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A first start opens a Pending step and invokes it")]
    public async Task First_start_opens_a_pending_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var start = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.Invoke, start.Outcome);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, step.Status);
        Assert.Equal(step.Version, start.Version);
    }

    [Fact(DisplayName = "A Pending ordinary step re-entered is invoked again and its row is left unchanged")]
    public async Task Re_entered_pending_step_is_invoked_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        var before = await ReadStepAsync(job, ct);

        var again = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.Invoke, again.Outcome);
        Assert.Equal(before.Version, again.Version);
        var after = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ModifiedAtUtc, after.ModifiedAtUtc);
    }

    [Fact(DisplayName = "An at-most-once step re-entered before completion is terminalized Interrupted and never invoked again")]
    public async Task At_most_once_re_entry_is_interrupted()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, (await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct)).Outcome);

        var reentered = await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct);

        Assert.Equal(StartJobStepOutcomeCode.Interrupted, reentered.Outcome);
        var interrupted = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Interrupted, interrupted.Status);

        // Interrupted is terminal: a later entry answers the same and writes nothing.
        Assert.Equal(StartJobStepOutcomeCode.Interrupted, (await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct)).Outcome);
        Assert.Equal(interrupted.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "A succeeded step replays its stored result without a write")]
    public async Task Succeeded_step_replays_its_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(CompleteJobStepOutcomeCode.Succeeded, (await CompleteAsync(job, succeeded: true, first.Version, ct)).Outcome);
        var stored = await ReadStepAsync(job, ct);

        var replay = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.ReplaySuccess, replay.Outcome);
        Assert.Equal(JobPayloadFormat.Json.Id, replay.ResultFormatId);
        Assert.Equal(Result, replay.Result);
        Assert.Equal(stored.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "An exhausted step is refused with its final reason, on every later entry")]
    public async Task Exhausted_step_is_refused_with_its_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct);
        Assert.Equal(CompleteJobStepOutcomeCode.Exhausted, (await CompleteAsync(job, succeeded: false, first.Version, ct)).Outcome);

        for (var entry = 0; entry < 2; entry++)
        {
            var refused = await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct);
            Assert.Equal(StartJobStepOutcomeCode.Exhausted, refused.Outcome);
            Assert.Equal("card declined", refused.FailureMessage);
        }
        Assert.Equal(StepStatusCode.Exhausted, (await ReadStepAsync(job, ct)).Status);
    }

    private static readonly byte[] Result = Encoding.UTF8.GetBytes("{\"receipt\":7}");

    // A job staged Executing, as a claim leaves it, so the step writes pass the execution fence.
    private async Task<ExecutionFence> EnqueueAsync(CancellationToken ct) =>
        await ExecutionFenceStaging.ExecutingAsync(
            Db,
            (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId,
            ct
        );

    private Task<CompleteJobStepResponse> CompleteAsync(ExecutionFence job, bool succeeded, int version, CancellationToken ct) =>
        Execution.CompleteJobStepAsync(
            succeeded
                ? new CompleteJobStepCommand(job, StepName, true, JobPayloadFormat.Json.Id, Result, null, version)
                : new CompleteJobStepCommand(job, StepName, false, 0, null, "card declined", version),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(ExecutionFence job, CancellationToken ct)
    {
        var jobId = job.JobId;
        return Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct));
    }
}
