using Xunit;

namespace Acta.Tests.Context;

/// <summary><c>ctx.EndsJob(ex)</c> answers whether the exception, left uncaught, ends the job, as the worker would land it.</summary>
public sealed class EndsJobTests
{
    public static TheoryData<Exception> EndTheJobOnAnyAttempt =>
        [
            new HandlerFailException("bad input"),
            new HandlerCancelException("superseded"),
            new SignalWaitTimeoutException("approval"),
            new StepInterruptedException("charge"),
            new StepFailedException("charge", "declined"),
            new NotSupportedException(),
            new NotImplementedException(),
            new JobFailedException(7, JobRef.New(), JobStatusCode.Failed),
            new ChildGroupException([]),
            new JobWaitTimeoutException(7, JobRef.New()),
            new JobResultMissingException(7, JobRef.New()),
        ];

    public static TheoryData<Exception> NeverEndTheJob =>
        [
            new SignalSuspendSignal("approval"),
            new SuspendSignal("cool-down", DateTime.UtcNow, reasonMessage: null),
            new RescheduleJobException(TimeSpan.FromMinutes(1)),
            new HandlerPauseException("hold"),
            new ExecutionFencedException("fenced"),
        ];

    [Theory]
    [MemberData(nameof(EndTheJobOnAnyAttempt))]
    public void An_exception_that_lands_terminal_ends_the_job_on_any_attempt(Exception exception) =>
        Assert.True(new RecordingJobContext().EndsJob(exception));

    [Theory]
    [MemberData(nameof(NeverEndTheJob))]
    public void A_wait_sleep_reschedule_pause_or_fence_never_ends_the_job(Exception exception) =>
        Assert.False(new RecordingJobContext { LastAttempt = true }.EndsJob(exception));

    [Fact]
    public void A_retryable_error_ends_the_job_on_the_last_attempt_only()
    {
        var error = new InvalidOperationException("timeout");

        Assert.False(new RecordingJobContext().EndsJob(error));
        Assert.True(new RecordingJobContext { LastAttempt = true }.EndsJob(error));
    }

    [Fact]
    public void A_cancelled_attempts_exception_never_ends_the_job()
    {
        using var attempt = new CancellationTokenSource();
        attempt.Cancel();

        Assert.False(
            new RecordingJobContext { Attempt = attempt, LastAttempt = true }.EndsJob(new OperationCanceledException(attempt.Token))
        );
    }
}
