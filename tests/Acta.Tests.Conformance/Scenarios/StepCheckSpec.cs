using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for check steps (<c>ctx.RunStepWithCheckAsync(name, body, check, ct)</c>). A step started and never
/// recorded, as after a crash between its effect and its record, asks its check first, in the same attempt or a later
/// one: a value it finds is recorded as the step's result and the body does not run, and nothing found runs the body. A
/// first start, and a start after a state reset, never asks. A wait inside the body fails the job at once. The probes
/// count body and check invocations per job id.
/// </summary>
[ConformanceSpec(
    "step.check",
    "A check step asks its check before running a started body again",
    Area = "Steps",
    Contract = "A started step with no recorded result asks its check: a found value is recorded in place of the body, nothing found runs it, and a fresh start never asks."
)]
[Deploys(
    "job-check-step",
    "job-check-step-at-most-once",
    "job-check-step-unrecorded",
    "job-check-step-value",
    "job-check-step-void",
    "job-check-step-wait"
)]
public sealed class StepCheckSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<StepCheckSpec>>
{
    private const string Staged = "receipt-staged";

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A first run runs the body once and never asks the check, and the replay asks neither")]
    public async Task First_run_never_asks_the_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ProceedAsync(enqueued, ct);

        Assert.Equal(JobCheckStepProbes.BodyReceipt, await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.False(JobCheckStepProbes.CheckInvocations.ContainsKey(enqueued.JobId));
    }

    [Fact(
        DisplayName = "A started step whose check finds the effect returns and records the found value, and a replay reads it as recorded"
    )]
    public async Task A_found_effect_is_returned_and_recorded_in_place_of_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(), ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(Staged, JobCheckStepProbes.Returned[enqueued.JobId]);
        var step = await ReadStepAsync(enqueued.JobId, "charge", ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal($"\"{Staged}\"", Encoding.UTF8.GetString(step.Result!));

        await ProceedAsync(enqueued, ct);
        Assert.Equal(Staged, await Jobs.GetResultAsync<string>(enqueued, ct));
        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);

        var replay = await Execution.StartJobStepAsync(await ExecutingAsync(enqueued, ct), "charge", atMostOnce: false, ct);
        Assert.Equal(StartJobStepOutcomeCode.ReplaySuccess, replay.Outcome);
        Assert.Equal(step.Result, replay.Result);
    }

    [Fact(DisplayName = "A found value is recorded at the version the started step holds")]
    public async Task A_found_value_is_recorded_at_the_step_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(), ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);

        // A re-entered ordinary step keeps its version, so the fact moves it itself: a completion at any other version loses.
        var jobId = enqueued.JobId;
        Assert.Equal(
            1,
            await Db.From<StepEntity>()
                .Where(s => s.JobId == jobId && s.StepName == "charge")
                .UpdateOnlyAsync(() => new StepEntity { Version = 7 }, ct)
        );

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var step = await ReadStepAsync(jobId, "charge", ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(8, step.Version);
        Assert.Equal(Staged, JobCheckStepProbes.Returned[jobId]);
    }

    [Fact(
        DisplayName = "An attempt lost between the check's answer and its record leaves the step started, and the next attempt asks again"
    )]
    public async Task A_found_value_lost_before_its_record_is_asked_for_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(), ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);
        var staged = (await ReadJobAsync(enqueued.JobId, ct)).ExecutionNumber;
        var (entered, release) = JobCheckStepProbes.HoldNextCheck(enqueued.JobId);

        // The job moves off the attempt while its check is answering, as a crash or a lapsed lease leaves it: the found
        // value's record is refused by the fence, and the run-once drive claims the job again.
        var run = Runtime.RunOnceAsync(enqueued, ct);
        await entered.WaitAsync(SpecWaits.Gate, ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, enqueued.JobId, ct);
        release();

        Assert.Equal(RunOnceStatus.Rearmed, await run);
        Assert.Equal(2, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal(staged + 2, (await ReadJobAsync(enqueued.JobId, ct)).ExecutionNumber);
        Assert.Equal($"\"{Staged}\"", Encoding.UTF8.GetString((await ReadStepAsync(enqueued.JobId, "charge", ct)).Result!));
    }

    [Fact(DisplayName = "A started step whose check finds nothing runs the body")]
    public async Task Nothing_found_runs_the_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(), ct);
        await StartedThenLostAsync(enqueued, "charge", effect: null, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobCheckStepProbes.BodyReceipt, JobCheckStepProbes.Returned[enqueued.JobId]);
        var step = await ReadStepAsync(enqueued.JobId, "charge", ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal($"\"{JobCheckStepProbes.BodyReceipt}\"", Encoding.UTF8.GetString(step.Result!));
    }

    [Fact(DisplayName = "A body that throws after its effect is answered by the check on the next attempt")]
    public async Task A_body_that_threw_after_its_effect_is_found_by_the_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(BodyThrows: true), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        Assert.False(JobCheckStepProbes.CheckInvocations.ContainsKey(enqueued.JobId));

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.Equal(StepStatusCode.Succeeded, (await ReadStepAsync(enqueued.JobId, "charge", ct)).Status);
    }

    [Fact(DisplayName = "A handler that catches the body's throw and runs the step again in the same attempt has the check asked")]
    public async Task The_check_is_asked_again_within_one_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(RunAgainInHandler: true), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.Equal(JobCheckStepProbes.BodyReceipt, JobCheckStepProbes.Returned[enqueued.JobId]);
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(1, job.ExecutionNumber);
        Assert.Equal(0, job.FailureCount);
    }

    [Fact(
        DisplayName = "A value-type check step returns and records the value its check found, and runs its body when the check finds none"
    )]
    public async Task A_value_type_check_step_records_what_it_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var found = await EnqueueAsync("job-check-step-value", ct);
        var missing = await EnqueueAsync("job-check-step-value", ct);
        await StartedThenLostAsync(found, "count", "42", ct);
        await StartedThenLostAsync(missing, "count", effect: null, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(found, ct));
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(missing, ct));

        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(found.JobId));
        Assert.Equal(42, JobCheckStepProbes.Returned[found.JobId]);
        Assert.Equal("42", Encoding.UTF8.GetString((await ReadStepAsync(found.JobId, "count", ct)).Result!));
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[missing.JobId]);
        Assert.Equal(7, JobCheckStepProbes.Returned[missing.JobId]);
        Assert.Equal("7", Encoding.UTF8.GetString((await ReadStepAsync(missing.JobId, "count", ct)).Result!));
        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[found.JobId]);
        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[missing.JobId]);
    }

    [Fact(DisplayName = "A no-result check step whose check answers true succeeds without its body, and false runs the body")]
    public async Task A_no_result_check_step_succeeds_on_a_found_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        var found = await EnqueueAsync("job-check-step-void", ct);
        var missing = await EnqueueAsync("job-check-step-void", ct);
        await StartedThenLostAsync(found, "refund", "refunded", ct);
        await StartedThenLostAsync(missing, "refund", effect: null, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(found, ct));
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(missing, ct));

        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(found.JobId));
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[missing.JobId]);
        foreach (var job in new[] { found, missing })
        {
            Assert.Equal(1, JobCheckStepProbes.CheckInvocations[job.JobId]);
            var step = await ReadStepAsync(job.JobId, "refund", ct);
            Assert.Equal(StepStatusCode.Succeeded, step.Status);
            Assert.Null(step.Result);
        }
    }

    [Fact(DisplayName = "A check that throws ends the attempt as a body's throw does, and the next attempt asks again")]
    public async Task A_check_that_throws_ends_the_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(CheckThrows: true), ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFailed, ct);
        Assert.Equal(EventReason.UnhandledException, EventDetail(failed, "reason"));
        Assert.Contains("did not answer the lookup", failed.EventMessage, StringComparison.Ordinal);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(2, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal($"\"{Staged}\"", Encoding.UTF8.GetString((await ReadStepAsync(enqueued.JobId, "charge", ct)).Result!));
    }

    [Fact(DisplayName = "A wait inside a check step's body fails the job at once in every form, and a restart asks the check")]
    public async Task A_wait_inside_a_check_step_body_fails_the_job_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var form in new[] { "reference", "value", "no result" })
        {
            foreach (var effectFirst in new[] { true, false })
            {
                var enqueued = await EnqueueAsync("job-check-step-wait", new CheckStepWait(form, effectFirst), ct);

                // The job has attempts left, and the refusal spends none of them: it is not retried.
                Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
                var refused = Assert.IsType<StepFailedException>(JobCheckStepProbes.RefusedWaits[enqueued.JobId]);
                Assert.Contains("WaitSignalAsync inside a check step's body is refused", refused.Message, StringComparison.Ordinal);
                var job = await ReadJobAsync(enqueued.JobId, ct);
                Assert.Equal(JobStatusCode.Failed, job.Status);
                Assert.Equal(1, job.ExecutionNumber);
                Assert.Equal(0, job.FailureCount);
                var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
                Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
                Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
                Assert.Equal(StepStatusCode.Pending, (await ReadStepAsync(enqueued.JobId, "pack", ct)).Status);

                // A restart asks the check. An effect the body applied before the wait is found, and the step ends without
                // the rest of its body; with none, the body runs into the refused wait again.
                Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "moved the wait", ct: ct)).Action);
                Assert.Equal(effectFirst ? RunOnceStatus.Completed : RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
                Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
                Assert.Equal(effectFirst ? 1 : 2, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
                Assert.Equal(
                    effectFirst ? StepStatusCode.Succeeded : StepStatusCode.Pending,
                    (await ReadStepAsync(enqueued.JobId, "pack", ct)).Status
                );
            }
        }
    }

    [Fact(DisplayName = "A handler that resets its state on every attempt never has the check asked")]
    public async Task A_reset_step_is_started_clean_on_every_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(BodyThrows: true, ResetFirst: true), ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);

        // Each attempt resets before the step, so each start opens the step anew, the effect the last one left included.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.False(JobCheckStepProbes.CheckInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal(2, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "A restart keeps a check step that was started and never recorded, and its next start asks the check")]
    public async Task A_restart_keeps_a_started_check_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step", new CheckStepFault(BodyFailsForGood: true), ct);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(StepStatusCode.Pending, (await ReadStepAsync(enqueued.JobId, "charge", ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, "provider answered", ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobCheckStepProbes.CheckInvocations[enqueued.JobId]);
        Assert.Equal(1, JobCheckStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobCheckStepProbes.BodyReceipt, JobCheckStepProbes.Returned[enqueued.JobId]);
        Assert.Equal(StepStatusCode.Succeeded, (await ReadStepAsync(enqueued.JobId, "charge", ct)).Status);
    }

    [Fact(DisplayName = "A found value that cannot be recorded fails the step with a message of its own")]
    public async Task A_found_value_that_cannot_be_recorded_fails_the_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step-unrecorded", ct);
        await StartedThenLostAsync(enqueued, "charge", effect: null, ct);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Contains("its check found its effect, and the value found was not recorded", failed.EventMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("its body ran", failed.EventMessage, StringComparison.Ordinal);
        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal(StepStatusCode.Pending, (await ReadStepAsync(enqueued.JobId, "charge", ct)).Status);
    }

    [Fact(DisplayName = "An at-most-once step re-entered before its record is still interrupted, its body not run")]
    public async Task An_at_most_once_step_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAsync("job-check-step-at-most-once", ct);
        await StartedThenLostAsync(enqueued, "charge", Staged, ct);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.False(JobCheckStepProbes.BodyInvocations.ContainsKey(enqueued.JobId));
        Assert.Equal(StepStatusCode.Interrupted, (await ReadStepAsync(enqueued.JobId, "charge", ct)).Status);
    }

    private Task<EnqueueJobResponse> EnqueueAsync(string jobName, CancellationToken ct) =>
        Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.None), ct);

    private Task<EnqueueJobResponse> EnqueueAsync<TInput>(string jobName, TInput input, CancellationToken ct) =>
        Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.Json(input)), ct);

    private async Task ProceedAsync(EnqueueJobResponse enqueued, CancellationToken ct)
    {
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "proceed", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
    }

    // The job staged Executing, as a claim leaves it, so the step writes pass the execution fence.
    private Task<ExecutionFence> ExecutingAsync(EnqueueJobResponse enqueued, CancellationToken ct) =>
        ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);

    // An attempt that started the step, had its effect when one is given, and was lost before the step was recorded.
    private async Task StartedThenLostAsync(EnqueueJobResponse enqueued, string step, string? effect, CancellationToken ct)
    {
        Assert.Equal(
            StartJobStepOutcomeCode.Invoke,
            (await Execution.StartJobStepAsync(await ExecutingAsync(enqueued, ct), step, false, ct)).Outcome
        );
        if (effect is not null)
        {
            JobCheckStepProbes.Effects[enqueued.JobId] = effect;
        }
        await ChaosSpecHelpers.SetReadyAsync(Db, enqueued.JobId, ct);
    }

    private async Task<StepEntity> ReadStepAsync(long jobId, string step, CancellationToken ct) =>
        Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == step).ToListAsync(ct));
}
