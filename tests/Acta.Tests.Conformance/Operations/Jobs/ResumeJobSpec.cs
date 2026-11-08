using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for resume's next run: a resumed job keeps the time it was due at, and one already due runs now.</summary>
[ConformanceSpec(
    "resume-job.next-run",
    "Resume keeps the job's due time and runs a past one now",
    Area = "Control",
    Contract = "Resume keeps a paused job's future next_run_at_utc and takes the database clock for one already past."
)]
public sealed class ResumeJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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
}
