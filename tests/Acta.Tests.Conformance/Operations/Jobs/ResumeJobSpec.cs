using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for resume's next run: an explicit instant pins it, none takes the database clock.</summary>
[ConformanceSpec(
    "resume-job.next-run",
    "Resume pins an explicit next run and otherwise takes the database clock",
    Area = "Control",
    Contract = "Resume pins an explicit next_run_at_utc and otherwise coalesces the next run to the database clock."
)]
public sealed class ResumeJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput(string reason) =>
        new(new JobControlActor(ActorCode.Operator, "test"), JobEventReasonCode.JobControlManual, reason);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        RuntimeStateStaging.SetStatusAsync(db, jobId, statusCode, ct);

    private static Task SetFailureCountAsync(IDbSession db, long jobId, short count, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET failure_count = @p_count WHERE job_id = @p_id",
            ct,
            ("@p_count", count),
            ("@p_id", jobId)
        );

    [Fact(DisplayName = "Resume with explicit next_run_at_utc pins the instant; null coalesces to DB-now")]
    public async Task Resume_explicit_next_run_pins_instant_null_coalesces_to_db_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("resume next-run branch");

        var explicitJob = await EnqueueAsync(ct);
        var defaultJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, explicitJob, (byte)JobStatusCode.Paused, ct);
        await SetJobStatusAsync(Db, defaultJob, (byte)JobStatusCode.Paused, ct);

        // Far-future instant with no sub-second component so the round-trip is lossless.
        var explicitNextRun = new DateTime(2027, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var outcomeExplicit = await Services.GetRequiredService<ResumeJobHandler>().ResumeAsync(explicitJob, input, explicitNextRun, ct);

        // A null next run coalesces to db_now, so both ends of the window are read from the same
        // clock that produced it; a process-clock bracket would be measuring container clock skew.
        var tBefore = (await NowAsync(ct)).AddSeconds(-1);
        var outcomeDefault = await Services.GetRequiredService<ResumeJobHandler>().ResumeAsync(defaultJob, input, null, ct);
        var tAfter = (await NowAsync(ct)).AddSeconds(1);

        Assert.Equal(ControlAction.Applied, outcomeExplicit.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeExplicit.Status);
        Assert.Equal(ControlAction.Applied, outcomeDefault.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeDefault.Status);

        var explicitState = await ReadJobAsync(explicitJob, ct);
        Assert.Equal(JobStatusCode.Ready, explicitState.Status);
        Assert.Equal(explicitNextRun, explicitState.NextRunAtUtc);

        var defaultState = await ReadJobAsync(defaultJob, ct);
        Assert.Equal(JobStatusCode.Ready, defaultState.Status);
        Assert.NotNull(defaultState.NextRunAtUtc);
        Assert.InRange(defaultState.NextRunAtUtc!.Value, tBefore, tAfter);
    }
}
