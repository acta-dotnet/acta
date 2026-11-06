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
    "Starting a step invokes, replays, suspends, or refuses from the step's state",
    Area = "Execution",
    Contract = "A step start invokes a new or due step as its next attempt, replays a success, suspends on a pending retry, and refuses an exhausted or interrupted step."
)]
public sealed class StartJobStepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string StepName = "charge";

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A first start opens a Pending step at attempt 1 and invokes it")]
    public async Task First_start_opens_a_pending_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var start = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.Invoke, start.Outcome);
        Assert.Equal(1, start.AttemptNumber);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, step.Status);
        Assert.Equal(1, step.AttemptNumber);
        Assert.Equal(step.Version, start.Version);
        Assert.Null(step.NextRetryAtUtc);
    }

    [Fact(DisplayName = "A Pending step re-entered with no retry instant is invoked again as the next attempt")]
    public async Task Re_entered_pending_step_is_invoked_as_the_next_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        var again = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.Invoke, again.Outcome);
        Assert.Equal(2, again.AttemptNumber);
        Assert.Equal(first.Version + 1, again.Version);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(2, step.AttemptNumber);
        Assert.Equal(again.Version, step.Version);
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
        Assert.Equal(1, interrupted.AttemptNumber);
        Assert.Equal(StepFailureCode.Interrupted, interrupted.FailureCode);

        // Interrupted is terminal: a later entry answers the same and writes nothing.
        Assert.Equal(StartJobStepOutcomeCode.Interrupted, (await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct)).Outcome);
        Assert.Equal(interrupted.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "A pending retry suspends until its instant, and once the instant passes the next attempt is invoked")]
    public async Task Pending_retry_suspends_until_its_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        var failed = await CompleteAsync(job, succeeded: false, first.Version, maxAttempts: 3, ct);
        Assert.Equal(CompleteJobStepOutcomeCode.RetryScheduled, failed.Outcome);

        var waiting = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.Suspend, waiting.Outcome);
        Assert.Equal(failed.NextRetryAtUtc, waiting.NextRetryAtUtc);
        Assert.Equal(1, (await ReadStepAsync(job, ct)).AttemptNumber);

        var due = DateTime.UtcNow.AddMinutes(-1);
        var jobId = job.JobId;
        Assert.Equal(
            1,
            await Db.From<StepEntity>()
                .Where(s => s.JobId == jobId && s.StepName == StepName)
                .UpdateOnlyAsync(() => new StepEntity { NextRetryAtUtc = due }, ct)
        );

        var retried = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(StartJobStepOutcomeCode.Invoke, retried.Outcome);
        Assert.Equal(2, retried.AttemptNumber);
    }

    [Fact(DisplayName = "A succeeded step replays its stored result without a new attempt or a write")]
    public async Task Succeeded_step_replays_its_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(
            CompleteJobStepOutcomeCode.Succeeded,
            (await CompleteAsync(job, succeeded: true, first.Version, maxAttempts: 3, ct)).Outcome
        );
        var stored = await ReadStepAsync(job, ct);

        var replay = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartJobStepOutcomeCode.ReplaySuccess, replay.Outcome);
        Assert.Equal(JobPayloadFormat.Json.Id, replay.ResultFormatId);
        Assert.Equal(Result, replay.Result);
        Assert.Equal(1, replay.AttemptNumber);
        Assert.Equal(stored.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "An exhausted step is refused with its final reason, on every later entry")]
    public async Task Exhausted_step_is_refused_with_its_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(
            CompleteJobStepOutcomeCode.Exhausted,
            (await CompleteAsync(job, succeeded: false, first.Version, maxAttempts: 1, ct)).Outcome
        );

        for (var entry = 0; entry < 2; entry++)
        {
            var refused = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
            Assert.Equal(StartJobStepOutcomeCode.Exhausted, refused.Outcome);
            Assert.Equal(1, refused.AttemptNumber);
            Assert.Equal(StepFailureCode.UnhandledException, refused.FailureCode);
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

    private Task<CompleteJobStepResponse> CompleteAsync(
        ExecutionFence job,
        bool succeeded,
        int version,
        int maxAttempts,
        CancellationToken ct
    ) =>
        Execution.CompleteJobStepAsync(
            succeeded
                ? new CompleteJobStepCommand(
                    job,
                    StepName,
                    true,
                    JobPayloadFormat.Json.Id,
                    Result,
                    null,
                    null,
                    0,
                    maxAttempts,
                    null,
                    version
                )
                : new CompleteJobStepCommand(
                    job,
                    StepName,
                    false,
                    0,
                    null,
                    StepFailureCode.UnhandledException,
                    "card declined",
                    DelaySeconds: 600,
                    maxAttempts,
                    RetryWindowSeconds: null,
                    version
                ),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(ExecutionFence job, CancellationToken ct)
    {
        var jobId = job.JobId;
        return Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct));
    }
}
