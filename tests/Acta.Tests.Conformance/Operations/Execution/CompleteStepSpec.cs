using System.Text;
using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>complete_step</c>, which records one invoked step attempt under the version its start returned.</summary>
[ConformanceSpec(
    "complete-step.decision",
    "Completing a step stores a success, schedules a retry, or exhausts the slot",
    Area = "Execution",
    Contract = "A step completion stores a success, schedules an in-budget retry, exhausts past the attempt or window budget, and writes nothing on a stale version."
)]
public sealed class CompleteStepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string StepName = "ship";
    private const string Reason = "carrier offline";
    private static readonly byte[] Result = Encoding.UTF8.GetBytes("{\"tracking\":\"x1\"}");

    private Acta.Runtime.Execution Execution => Services.GetRequiredService<Acta.Runtime.Execution>();

    [Fact(DisplayName = "A success stores the result, marks the slot Succeeded, and clears an earlier failure's reason and retry instant")]
    public async Task Success_stores_the_result_and_clears_the_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var first = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(CompleteStepOutcomeCode.RetryScheduled, (await FailAsync(job, first.Version, maxAttempts: 3, null, ct)).Outcome);
        var due = DateTime.UtcNow.AddMinutes(-1);
        await Db.From<StepEntity>()
            .Where(s => s.JobId == job && s.Name == StepName)
            .UpdateOnlyAsync(() => new StepEntity { NextRetryAtUtc = due }, ct);
        var second = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        Assert.Equal(2, second.AttemptNumber);

        var done = await Execution.CompleteStepAsync(
            new CompleteStepCommand(job, StepName, true, JobPayloadFormat.Json.Id, Result, null, null, 0, 3, null, second.Version),
            ct
        );

        Assert.Equal(new CompleteStepResponse(CompleteStepOutcomeCode.Succeeded, null), done);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(JobPayloadFormat.Json.Id, step.ResultFormatId);
        Assert.Equal(Result, step.Result);
        Assert.Null(step.ReasonCode);
        Assert.Null(step.ReasonMessage);
        Assert.Null(step.NextRetryAtUtc);
        Assert.Equal(second.Version + 1, step.Version);
    }

    [Fact(DisplayName = "An in-budget failure keeps the slot Pending with a retry instant one delay out and the reason recorded")]
    public async Task In_budget_failure_schedules_a_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var start = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        var failed = await FailAsync(job, start.Version, maxAttempts: 3, null, ct);

        Assert.Equal(CompleteStepOutcomeCode.RetryScheduled, failed.Outcome);
        var retryAt = Assert.NotNull(failed.NextRetryAtUtc);
        Assert.InRange(retryAt, dbNow.AddSeconds(599), dbNow.AddSeconds(660));
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, step.Status);
        Assert.Equal(retryAt, step.NextRetryAtUtc);
        Assert.Equal(EventReasonCode.JobUnhandledException, step.ReasonCode);
        Assert.Equal(Reason, step.ReasonMessage);
        Assert.Equal(1, step.AttemptNumber);
        Assert.Equal(start.Version + 1, step.Version);
    }

    [Fact(DisplayName = "A failure on the last allowed attempt exhausts the slot with its reason and no retry instant")]
    public async Task Failure_on_the_last_attempt_exhausts_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var start = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);

        var exhausted = await FailAsync(job, start.Version, maxAttempts: 1, null, ct);

        Assert.Equal(new CompleteStepResponse(CompleteStepOutcomeCode.Exhausted, null), exhausted);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Exhausted, step.Status);
        Assert.Null(step.NextRetryAtUtc);
        Assert.Equal(EventReasonCode.JobUnhandledException, step.ReasonCode);
        Assert.Equal(Reason, step.ReasonMessage);
    }

    [Fact(DisplayName = "A retry that would land past the retry window exhausts the slot with attempts still left")]
    public async Task Retry_past_the_window_exhausts_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = await EnqueueAsync(ct);
        var inside = await EnqueueAsync(ct);
        var outsideStart = await Execution.StartStepAsync(outside, StepName, atMostOnce: false, ct);
        var insideStart = await Execution.StartStepAsync(inside, StepName, atMostOnce: false, ct);

        // The delay is ten minutes either way, so only the window decides.
        var pastWindow = await FailAsync(outside, outsideStart.Version, maxAttempts: 5, retryWindowSeconds: 60, ct);
        var withinWindow = await FailAsync(inside, insideStart.Version, maxAttempts: 5, retryWindowSeconds: 3600, ct);

        Assert.Equal(CompleteStepOutcomeCode.Exhausted, pastWindow.Outcome);
        Assert.Equal(StepStatusCode.Exhausted, (await ReadStepAsync(outside, ct)).Status);
        Assert.Equal(CompleteStepOutcomeCode.RetryScheduled, withinWindow.Outcome);
        Assert.Equal(StepStatusCode.Pending, (await ReadStepAsync(inside, ct)).Status);
    }

    [Fact(DisplayName = "A success or a failure carrying a stale version reports StaleVersion and writes nothing")]
    public async Task Stale_version_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var stale = await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        // A second start of the same slot, as a concurrent execution of the job would make, moves the version on.
        await Execution.StartStepAsync(job, StepName, atMostOnce: false, ct);
        var before = await ReadStepAsync(job, ct);

        var success = await Execution.CompleteStepAsync(
            new CompleteStepCommand(job, StepName, true, JobPayloadFormat.Json.Id, Result, null, null, 0, 3, null, stale.Version),
            ct
        );
        var failure = await FailAsync(job, stale.Version, maxAttempts: 3, null, ct);

        Assert.Equal(new CompleteStepResponse(CompleteStepOutcomeCode.StaleVersion, null), success);
        Assert.Equal(new CompleteStepResponse(CompleteStepOutcomeCode.StaleVersion, null), failure);
        var after = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Pending, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.Result);
        Assert.Null(after.ReasonCode);
        Assert.Null(after.NextRetryAtUtc);
    }

    private async Task<long> EnqueueAsync(CancellationToken ct) =>
        (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId;

    private Task<CompleteStepResponse> FailAsync(long jobId, int version, int maxAttempts, int? retryWindowSeconds, CancellationToken ct) =>
        Execution.CompleteStepAsync(
            new CompleteStepCommand(
                jobId,
                StepName,
                Succeeded: false,
                ResultFormatId: 0,
                Result: null,
                EventReasonCode.JobUnhandledException,
                Reason,
                DelaySeconds: 600,
                maxAttempts,
                retryWindowSeconds,
                version
            ),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.Name == StepName).ToListAsync(ct));
}
