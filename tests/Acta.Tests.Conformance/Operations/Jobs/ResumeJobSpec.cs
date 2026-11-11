using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for resume's next run: a resumed job keeps the time it was due at, and one already due, or suspended on a
/// signal or child wait, runs now.
/// </summary>
[ConformanceSpec(
    "resume-job.next-run",
    "Resume keeps the job's due time unless it has passed or is a wait's timeout",
    Area = "Control",
    Contract = "Resume keeps a paused job's future next_run_at_utc, a sleep's included, and runs now one already past or paused while suspended on a signal or child wait."
)]
[Deploys("add-numbers", "job-sleep-basic", "job-wait-signal-timeout")]
public sealed class ResumeJobSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ResumeJobSpec>>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private EnqueueJobRequest AddNumbers() => new(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)));

    [Fact(DisplayName = "Resume keeps a delayed job's future next run, and a job already due runs now")]
    public async Task Resume_keeps_a_future_next_run_and_runs_a_past_one_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var delayed = await Jobs.EnqueueAsync(AddNumbers() with { DelaySeconds = 3600 }, ct);
        var due = await Jobs.EnqueueAsync(AddNumbers(), ct);
        var dueAt = (await ReadJobAsync(delayed.JobId, ct)).NextRunAtUtc;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(delayed, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(due, ct: ct)).Action);

        // A past next run takes db_now, so both ends of the window are read from that clock.
        var tBefore = (await NowAsync(ct)).AddSeconds(-1);
        Assert.Equal(JobStatusCode.Ready, (await Jobs.ResumeAsync(delayed, ct: ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await Jobs.ResumeAsync(due, ct: ct)).Status);
        var tAfter = (await NowAsync(ct)).AddSeconds(1);

        Assert.Equal(dueAt, (await ReadJobAsync(delayed.JobId, ct)).NextRunAtUtc);
        var runsAt = (await ReadJobAsync(due.JobId, ct)).NextRunAtUtc;
        Assert.NotNull(runsAt);
        Assert.InRange(runsAt!.Value, tBefore, tAfter);
    }

    [Fact(DisplayName = "Resume keeps a paused durable sleep's due time")]
    public async Task Resume_keeps_a_paused_durable_sleeps_due_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var sleeping = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-sleep-basic", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(sleeping, ct));
        var wakesAt = (await ReadJobAsync(sleeping.JobId, ct)).NextRunAtUtc;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(sleeping, ct: ct)).Action);

        Assert.Equal(JobStatusCode.Ready, (await Jobs.ResumeAsync(sleeping, ct: ct)).Status);

        Assert.Equal(wakesAt, (await ReadJobAsync(sleeping.JobId, ct)).NextRunAtUtc);
    }

    [Fact(DisplayName = "Resume runs a paused signal wait now, to see a signal raised while paused or suspend again until its timeout")]
    public async Task Resume_runs_a_paused_signal_wait_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var raised = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        var waiting = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(raised, ct));
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(waiting, ct));
        var timeoutAt = (await ReadJobAsync(waiting.JobId, ct)).NextRunAtUtc;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(raised, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(waiting, ct: ct)).Action);
        // A raise on a paused job stores the signal and leaves the job Paused.
        Assert.Equal(JobStatusCode.Paused, (await Jobs.RaiseSignalAsync(raised, "go", JobPayload.None, ct: ct)).Status);

        var tBefore = (await NowAsync(ct)).AddSeconds(-1);
        Assert.Equal(JobStatusCode.Ready, (await Jobs.ResumeAsync(raised, ct: ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await Jobs.ResumeAsync(waiting, ct: ct)).Status);
        var tAfter = (await NowAsync(ct)).AddSeconds(1);

        Assert.InRange((await ReadJobAsync(raised.JobId, ct)).NextRunAtUtc!.Value, tBefore, tAfter);
        Assert.InRange((await ReadJobAsync(waiting.JobId, ct)).NextRunAtUtc!.Value, tBefore, tAfter);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(raised, ct));
        Assert.Equal(1, await CountVariableAsync(raised.JobId, "ran.after", ct));
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(waiting, ct));
        var suspended = await ReadJobAsync(waiting.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, suspended.Status);
        Assert.Equal(timeoutAt, suspended.NextRunAtUtc);
    }
}
