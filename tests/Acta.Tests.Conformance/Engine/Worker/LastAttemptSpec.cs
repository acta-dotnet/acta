using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for <c>ctx.IsLastAttempt</c>: it reads the rule the completion applies to a retryable failure, the
/// failures the job already charged against its <c>MaxAttempts</c> and the Strict deadline, never the execution number.
/// </summary>
[ConformanceSpec(
    "execution.last-attempt",
    "IsLastAttempt says whether a failure of this attempt ends the job for good",
    Area = "Execution",
    Contract = "IsLastAttempt is true when a failure would end the job, by its spent budget or a Strict deadline, and a resume that spent nothing leaves it false."
)]
[Deploys("last-attempt-budget", "last-attempt-deadline", "last-attempt-jitter")]
public sealed class LastAttemptSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<LastAttemptSpec>>
{
    [Fact(DisplayName = "IsLastAttempt is false before the last attempt and on a resume that spent nothing, and true on the last attempt")]
    public async Task Is_last_attempt_follows_the_spent_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "last-attempt-budget", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct)).Action);

        // Execution 2 is a resume: its number reaches MaxAttempts, but the wait spent none of the budget.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, (await ReadJobAsync(enqueued.JobId, ct)).FailureCount);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.False(LastAttemptProbes.Reads[(enqueued.JobId, 1)]);
        Assert.False(LastAttemptProbes.Reads[(enqueued.JobId, 2)]);
        Assert.True(LastAttemptProbes.Reads[(enqueued.JobId, 3)]);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "IsLastAttempt is true when the retry's backoff would pass a Strict deadline")]
    public async Task Is_last_attempt_when_the_retry_would_pass_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "last-attempt-deadline", JobPayload.None), ct);

        await Runtime.RunOnceAsync(enqueued, ct);

        // Four attempts of the budget remain, but the completion cancels the job rather than re-arm it past the deadline.
        Assert.True(LastAttemptProbes.Reads[(enqueued.JobId, 1)]);
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, job.Status);
        Assert.Equal(0, job.FailureCount);
    }

    [Fact(DisplayName = "IsLastAttempt and the completion decide on one jittered backoff draw near a Strict deadline")]
    public async Task Is_last_attempt_shares_the_completions_backoff_draw()
    {
        var ct = TestContext.Current.CancellationToken;

        // Twelve trials that agree prove one shared draw: two independent draws disagree about half the time.
        var conclusive = 0;
        for (var trial = 0; conclusive < 12; trial++)
        {
            Assert.True(trial < 60, $"only {conclusive} of {trial} trials drew a backoff clear of the deadline.");
            var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "last-attempt-jitter", JobPayload.None), ct);
            await Runtime.RunOnceAsync(enqueued, ct);

            var (last, untilDeadline, context) = LastAttemptProbes.JitterReads[enqueued.JobId];
            var delay = TimeSpan.FromSeconds(Assert.IsType<RuntimeJobContext>(context).FailureRetry!.Value.DelaySeconds);
            // The completion reads the deadline again later, so a draw within a minute of it may decide the other way.
            if ((delay - untilDeadline!.Value).Duration() < TimeSpan.FromMinutes(1))
            {
                continue;
            }

            conclusive++;
            Assert.Equal(delay > untilDeadline.Value, last);
            Assert.Equal(last ? JobStatusCode.Cancelled : JobStatusCode.Ready, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        }
    }
}
