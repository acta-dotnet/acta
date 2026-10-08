using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for pause's status guard: a suspended job pauses, and a paused one pauses again.</summary>
[ConformanceSpec(
    "pause-job.state-guard",
    "Pause applies from Suspended and again from Paused",
    Area = "Control",
    Contract = "Pause applies to a Suspended job, and pausing a Paused job applies again rather than rejecting."
)]
public sealed class PauseJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput(string reason) =>
        new(new JobControlActor(ActorCode.Operator, "test"), EventReasonCode.JobControlManual, reason);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        RuntimeStateStaging.SetStatusAsync(db, jobId, statusCode, ct);

    private static Task SetFailureCountAsync(IDbSession db, long jobId, short count, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET failure_count = @p_count WHERE job_id = @p_id",
            ct,
            ("@p_count", count),
            ("@p_id", jobId)
        );

    [Fact(DisplayName = "Pause from Suspended is Applied and re-pause from Paused is also Applied")]
    public async Task Pause_from_Suspended_and_repause_from_Paused_both_Applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("pause test");

        var jobId = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobId, (byte)JobStatusCode.Suspended, ct);

        var firstPause = await Services.GetRequiredService<PauseJobHandler>().PauseAsync(jobId, input, ct);
        Assert.Equal(ControlAction.Applied, firstPause.Action);
        Assert.Equal(JobStatusCode.Paused, firstPause.Status);

        var afterFirst = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Paused, afterFirst.Status);

        var secondPause = await Services.GetRequiredService<PauseJobHandler>().PauseAsync(jobId, input, ct);
        Assert.Equal(ControlAction.Applied, secondPause.Action);
        Assert.Equal(JobStatusCode.Paused, secondPause.Status);

        var afterSecond = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Paused, afterSecond.Status);
    }
}
