using System.Diagnostics;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// Pins which outcome an attempt lands when something other than the handler's return decides it: a control verb
/// called inside a step body, a step exhausted before this attempt, and a service that throws while the attempt's
/// scope is disposed after the outcome is decided; and that the handler's failure line says what lands, or that recovery
/// reclaims the job when the worker stops before the write.
/// </summary>
public sealed class WorkerExecutorOutcomeTests
{
    [Fact]
    public async Task A_fail_inside_a_step_body_fails_the_job_not_the_step()
    {
        var harness = new WorkerExecutorHarness();

        var outcome = await harness.RunAsync(
            static (ctx, ct) =>
                ctx.RunStepAsync(WorkerExecutorHarness.StepName, async token => await ctx.FailAsync("bad input", token), ct: ct)
        );

        Assert.Equal(RunOnceStatus.Failed, outcome);
        Assert.Equal((byte)JobStatusCode.Failed, harness.Completion.HandlerStatusCode);
        Assert.Equal(EventReason.HandlerFailed, harness.Completion.Reason);
        Assert.Equal(EventCode.JobFailed, harness.Completion.EventCode);
    }

    [Fact]
    public async Task An_uncaught_exhausted_step_fails_the_job_at_once_instead_of_retrying_it()
    {
        // Every retry would replay the exhausted step and throw again, so the budget is not spent on it.
        var harness = new WorkerExecutorHarness(maxAttempts: 10);

        var outcome = await harness.RunAsync(static (_, _) => throw new StepExhaustedException(WorkerExecutorHarness.StepName, "declined"));

        Assert.Equal(RunOnceStatus.Failed, outcome);
        Assert.Equal(EventReason.NonRetryableException, harness.Completion.Reason);
        Assert.Null(harness.Completion.RescheduleEventCode);
    }

    // Every retry would build the handler from the same registrations, while the handler's own
    // InvalidOperationException, its base type, still retries.
    [Theory]
    [InlineData(true, EventReason.NonRetryableException)]
    [InlineData(false, EventReason.UnhandledException)]
    public async Task A_handler_DI_cannot_build_fails_the_job_at_once(bool activation, string reason)
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 10);
        var missing = new InvalidOperationException("Unable to resolve service for type 'Clock' while attempting to activate 'Biller'.");
        Exception thrown = activation ? new HandlerActivationException(typeof(WorkerExecutorOutcomeTests), missing) : missing;

        var outcome = await harness.RunAsync((_, _) => throw thrown);

        Assert.Equal(activation ? RunOnceStatus.Failed : RunOnceStatus.Rearmed, outcome);
        Assert.Equal(reason, harness.Completion.Reason);
        Assert.Equal(thrown.Message, harness.Completion.EventMessage);
    }

    [Fact]
    public async Task A_handler_that_throws_with_budget_left_logs_its_ref_attempt_and_retry()
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 3, failureCount: 1);
        var thrown = new InvalidOperationException("503 from the carrier");

        var outcome = await harness.RunAsync((_, _) => throw thrown);

        Assert.Equal(RunOnceStatus.Rearmed, outcome);
        var line = Assert.Single(harness.Log, entry => entry.Message.StartsWith("Handler for job", StringComparison.Ordinal));
        Assert.Matches(
            $@"^Handler for job \(harness-job\) \({harness.JobRef}\) threw on attempt \(2 of 3\); the job retries in {harness.Completion.RescheduleDelaySeconds * 1000}ms\.$",
            line.Message
        );
        Assert.Same(thrown, line.Exception);
    }

    [Theory]
    [InlineData(2, "attempt 3 of 3 spent the budget, so the job failed for good")]
    [InlineData(0, "NotImplementedException is never retried, so the job failed for good")]
    public async Task A_handler_that_throws_for_the_last_time_logs_that_the_job_does_not_retry(int failureCount, string why)
    {
        var harness = new WorkerExecutorHarness(maxAttempts: 3, failureCount: failureCount);
        Exception thrown = failureCount == 0 ? new NotImplementedException() : new InvalidOperationException();

        var outcome = await harness.RunAsync((_, _) => throw thrown);

        Assert.Equal(RunOnceStatus.Failed, outcome);
        var line = Assert.Single(harness.Log, entry => entry.Message.StartsWith("Handler for job", StringComparison.Ordinal));
        Assert.Equal($"Handler for job (harness-job) ({harness.JobRef}) threw, and the job does not retry ({why}).", line.Message);
        Assert.Same(thrown, line.Exception);
    }

    [Fact]
    public async Task A_handler_whose_retry_passes_the_deadline_by_its_completion_logs_that_the_job_is_cancelled()
    {
        // The handler throws with its one-second retry still inside the Strict deadline, and the attempt's services take
        // long enough to dispose that, by the time the completion is planned, the retry would land past it. A start-up
        // slow enough to pass the deadline cancels the job at admission, which leaves nothing to judge, so the trial
        // runs again.
        var trials = Stopwatch.StartNew();
        var thrown = new InvalidOperationException("503 from the carrier");
        var handlerRan = false;
        WorkerExecutorHarness harness;
        RunOnceStatus outcome;
        do
        {
            Assert.True(trials.Elapsed < TimeSpan.FromSeconds(60), "no trial ran its handler before its deadline passed.");
            harness = new WorkerExecutorHarness(
                deadlineAtUtc: DateTime.UtcNow.AddMilliseconds(1200),
                scopeDisposalTime: TimeSpan.FromMilliseconds(300),
                backoff: "1s"
            );
            outcome = await harness.RunAsync(
                (_, _) =>
                {
                    handlerRan = true;
                    throw thrown;
                }
            );
        } while (!handlerRan);

        Assert.Equal(RunOnceStatus.Completed, outcome);
        Assert.Equal(EventReason.DeadlineExceeded, harness.Completion.Reason);
        var line = Assert.Single(harness.Log, entry => entry.Message.StartsWith("Handler for job", StringComparison.Ordinal));
        Assert.Equal(
            $"Handler for job (harness-job) ({harness.JobRef}) threw, and the job does not retry (a retry would pass the job's deadline, so the job is cancelled).",
            line.Message
        );
        Assert.Same(thrown, line.Exception);
    }

    [Fact]
    public async Task A_failure_the_worker_stops_before_handing_over_logs_once_that_the_job_is_reclaimed()
    {
        // The finalizer never plans this completion, so the stop writes the line the plan would have written.
        var harness = new WorkerExecutorHarness(maxAttempts: 3, failureCount: 1);
        var thrown = new InvalidOperationException("503 from the carrier");

        await harness.RunAndStopAtHandOffAsync((_, _) => throw thrown);

        Assert.Empty(harness.Submitted);
        var line = Assert.Single(harness.Log, entry => entry.Message.StartsWith("Handler for job", StringComparison.Ordinal));
        Assert.Equal(
            $"Handler for job (harness-job) ({harness.JobRef}) threw on attempt (2 of 3), and the worker stopped before writing the failure; the job is reclaimed after its lease expires.",
            line.Message
        );
        Assert.Same(thrown, line.Exception);
    }

    [Theory]
    [InlineData("timeout", "Handler for job (harness-job) ({ref}) exceeded its execution timeout.")]
    [InlineData("abort", "WorkerRuntime: job ({ref}) attempt aborted mid-flight; handler stopped cooperatively.")]
    [InlineData("input", "Input deserialization for job (harness-job) ({ref}) failed; transitioning the attempt to Failed.")]
    public async Task A_timed_out_aborted_or_unreadable_attempt_logs_the_job_by_its_ref(string failure, string expected)
    {
        var harness = new WorkerExecutorHarness(inputDeserializationThrows: failure == "input");

        await harness.RunAsync(
            async (_, ct) =>
            {
                if (failure == "timeout")
                {
                    harness.TimeOutAttempt();
                }
                else
                {
                    harness.AbortAttempt();
                }
                await Task.Delay(Timeout.Infinite, ct);
            }
        );

        Assert.Contains(
            harness.Log,
            entry => entry.Message == expected.Replace("{ref}", harness.JobRef.ToString(), StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task A_service_that_throws_on_disposal_leaves_the_decided_outcome_standing()
    {
        var harness = new WorkerExecutorHarness(scopeDisposalThrows: true);

        var outcome = await harness.RunAsync();

        Assert.Equal(RunOnceStatus.Completed, outcome);
        Assert.True(harness.Completion.Succeeded);
        Assert.Contains(harness.Log, entry => entry.Message.Contains("disposing the attempt's services threw", StringComparison.Ordinal));
    }
}
