using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>start_step</c>, which opens or reads a durable step slot and decides what the step does on this entry.</summary>
[ConformanceSpec(
    "start-step.decision",
    "Starting a step invokes, replays, suspends, or refuses from the slot's state",
    Area = "Execution",
    Contract = "A step start invokes a new or due slot as its next attempt, replays a success, suspends on a pending retry, and refuses an exhausted or interrupted slot."
)]
public sealed class StartStepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string StepName = "charge";

    private Acta.Execution Execution => Services.GetRequiredService<Acta.Execution>();

    [Fact(DisplayName = "A first start opens a Pending slot at attempt 1 and invokes it")]
    public async Task First_start_opens_a_pending_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);

        var start = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartStepOutcomeCode.Invoke, start.Outcome);
        Assert.Equal(1, start.AttemptNumber);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, step.Status);
        Assert.Equal(1, step.AttemptNumber);
        Assert.Equal(step.Version, start.Version);
        Assert.Null(step.NextRetryAtUtc);
    }

    [Fact(DisplayName = "A Pending slot re-entered with no retry instant is invoked again as the next attempt")]
    public async Task Re_entered_pending_slot_is_invoked_as_the_next_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        var again = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartStepOutcomeCode.Invoke, again.Outcome);
        Assert.Equal(2, again.AttemptNumber);
        Assert.Equal(first.Version + 1, again.Version);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(2, step.AttemptNumber);
        Assert.Equal(again.Version, step.Version);
    }

    [Fact(DisplayName = "An at-most-once slot re-entered before completion is terminalized Interrupted and never invoked again")]
    public async Task At_most_once_re_entry_is_interrupted()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, (await Execution.StartStepAsync(job, StepName, atMostOnce: true, ct)).Outcome);

        var reentered = await Execution.StartStepAsync(job, StepName, atMostOnce: true, ct);

        Assert.Equal(StartStepOutcomeCode.Interrupted, reentered.Outcome);
        var interrupted = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Interrupted, interrupted.Status);
        Assert.Equal(1, interrupted.AttemptNumber);
        Assert.Equal(EventReasonCode.JobStepInterrupted, interrupted.ReasonCode);

        // Interrupted is terminal: a later entry answers the same and writes nothing.
        Assert.Equal(StartStepOutcomeCode.Interrupted, (await Execution.StartStepAsync(job, StepName, atMostOnce: true, ct)).Outcome);
        Assert.Equal(interrupted.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "A pending retry suspends until its instant, and once the instant passes the next attempt is invoked")]
    public async Task Pending_retry_suspends_until_its_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        var failed = await CompleteAsync(job, succeeded: false, first.Version, maxAttempts: 3, ct);
        Assert.Equal(CompleteStepOutcomeCode.RetryScheduled, failed.Outcome);

        var waiting = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartStepOutcomeCode.Suspend, waiting.Outcome);
        Assert.Equal(failed.NextRetryAtUtc, waiting.NextRetryAtUtc);
        Assert.Equal(1, (await ReadStepAsync(job, ct)).AttemptNumber);

        var due = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(
            1,
            await Db.From<StepEntity>()
                .Where(s => s.JobId == job && s.Name == StepName)
                .UpdateOnlyAsync(() => new StepEntity { NextRetryAtUtc = due }, ct)
        );

        var retried = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, retried.Outcome);
        Assert.Equal(2, retried.AttemptNumber);
    }

    [Fact(DisplayName = "A succeeded slot replays its stored result without a new attempt or a write")]
    public async Task Succeeded_slot_replays_its_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(
            CompleteStepOutcomeCode.Succeeded,
            (await CompleteAsync(job, succeeded: true, first.Version, maxAttempts: 3, ct)).Outcome
        );
        var stored = await ReadStepAsync(job, ct);

        var replay = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        Assert.Equal(StartStepOutcomeCode.ReplaySuccess, replay.Outcome);
        Assert.Equal(JobPayloadFormat.Json.Id, replay.ResultFormatId);
        Assert.Equal(Result, replay.Result);
        Assert.Equal(1, replay.AttemptNumber);
        Assert.Equal(stored.Version, (await ReadStepAsync(job, ct)).Version);
    }

    [Fact(DisplayName = "An exhausted slot is refused with its final reason, on every later entry")]
    public async Task Exhausted_slot_is_refused_with_its_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(
            CompleteStepOutcomeCode.Exhausted,
            (await CompleteAsync(job, succeeded: false, first.Version, maxAttempts: 1, ct)).Outcome
        );

        for (var entry = 0; entry < 2; entry++)
        {
            var refused = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
            Assert.Equal(StartStepOutcomeCode.Exhausted, refused.Outcome);
            Assert.Equal(1, refused.AttemptNumber);
            Assert.Equal(EventReasonCode.JobUnhandledException, refused.ReasonCode);
            Assert.Equal("card declined", refused.ReasonMessage);
        }
        Assert.Equal(StepStatusCode.Exhausted, (await ReadStepAsync(job, ct)).Status);
    }

    private static readonly byte[] Result = Encoding.UTF8.GetBytes("{\"receipt\":7}");

    private async Task<long> EnqueueAsync(CancellationToken ct) =>
        (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId;

    private Task<CompleteStepResponse> CompleteAsync(long jobId, bool succeeded, int version, int maxAttempts, CancellationToken ct) =>
        Execution.CompleteStepAsync(
            succeeded
                ? new CompleteStepCommand(
                    jobId,
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
                : new CompleteStepCommand(
                    jobId,
                    StepName,
                    false,
                    0,
                    null,
                    EventReasonCode.JobUnhandledException,
                    "card declined",
                    DelaySeconds: 600,
                    maxAttempts,
                    RetryWindowSeconds: null,
                    version
                ),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.Name == StepName).ToListAsync(ct));
}
