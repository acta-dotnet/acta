using Acta.Modules.Execution;
using Acta.Modules.Execution.Jobs;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Operator UpdateJobInputAsync: amends a job's stored input payload. Allowed in any status except
/// Dispatched/Executing (a mid-flight handler may already have read the input); the transition is
/// audited (job.input-amended) with the full previous payload preserved in the event detail.
/// </summary>
[ConformanceSpec(
    "job.update-input",
    "Operator update-input amends stored input and preserves the previous payload.",
    Area = "Control",
    Contract = "UpdateJobInput replaces a job's input in any status except Dispatched/Executing and audits job.input-amended with the full previous payload in the detail.",
    Arrange = "A Ready job, an executing job, a dispatched job, a failed job, and no job for an unknown lookup.",
    Act = "UpdateJobInput is invoked with a new payload against each job, and a restarted failed job is re-run.",
    Assert = "Ready and failed jobs adopt the new input with an audited old-payload event, in-flight jobs are rejected unchanged, and the unknown lookup is NotFound."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.UpdateJobInputAsync))]
public abstract class UpdateJobInputSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "UpdateJobInput amends a Ready job's input and audits the previous payload in the event detail")]
    public async Task Amend_ready_job_applies_new_input_and_audits_old_payload()
    {
        var ct = TestContext.Current.CancellationToken;

        var oldInput = JobPayload.Json(new AddNumbers(2, 3));
        var newInput = JobPayload.Json(new AddNumbers(7, 8));
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", oldInput), ct);

        var result = await Jobs.UpdateJobInputAsync(enqueued, newInput, "corrected operands", "spec-actor", ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);

        var job = await ReadJobRowAsync(enqueued.JobId, ct);
        Assert.Equal(newInput.Format.Id, job.InputFormatId);
        Assert.Equal(newInput.Data.ToArray(), job.Input);

        var evt = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobInputAmended, ct);
        Assert.Equal("spec-actor", evt.ActorKey);
        Assert.Equal("corrected operands", evt.ReasonMessage);
        Assert.Equal(oldInput.Format.Id, evt.DetailFormatId);
        Assert.Equal(oldInput.Data.ToArray(), evt.Detail);
    }

    [Fact(DisplayName = "UpdateJobInput rejects a Dispatched or Executing job and leaves its input unchanged")]
    public async Task Amend_in_flight_job_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var newInput = JobPayload.Json(new AddNumbers(9, 9));

        foreach (var status in new[] { JobStatusCode.Dispatched, JobStatusCode.Executing })
        {
            var oldInput = JobPayload.Json(new AddNumbers(1, 1));
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", oldInput), ct);
            await SetJobStatusAsync(enqueued.JobId, (byte)status, ct);

            var result = await Jobs.UpdateJobInputAsync(enqueued, newInput, "should not apply", ct: ct);

            Assert.Equal(JobControlAction.Rejected, result.Action);
            Assert.Equal(status, result.Status);

            var job = await ReadJobRowAsync(enqueued.JobId, ct);
            Assert.Equal(oldInput.Data.ToArray(), job.Input);
            Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobInputAmended, ct));
        }
    }

    [Fact(DisplayName = "UpdateJobInput on a Failed job feeds the new input to the handler after RestartAsync")]
    public async Task Amend_failed_job_then_restart_runs_with_new_input()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        await SetJobStatusAsync(enqueued.JobId, (byte)JobStatusCode.Failed, ct);

        var amend = await Jobs.UpdateJobInputAsync(enqueued, JobPayload.Json(new AddNumbers(10, 20)), "retry with new operands", ct: ct);
        Assert.Equal(JobControlAction.Applied, amend.Action);

        var restart = await Jobs.RestartAsync(enqueued, ct: ct);
        Assert.Equal(JobControlAction.Applied, restart.Action);
        Assert.Equal(JobStatusCode.Ready, restart.Status);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued.JobId, ct));

        var typed = await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct);
        Assert.NotNull(typed);
        Assert.Equal(30, typed!.Sum);
    }

    [Fact(DisplayName = "UpdateJobInput stores the new payload's format id, so a text job amends as text")]
    public async Task Amend_stores_the_payloads_format_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var oldInput = JobPayload.FromBytes(JobPayloadFormat.Text, System.Text.Encoding.UTF8.GetBytes("first"));
        var newInput = JobPayload.FromBytes(JobPayloadFormat.Text, System.Text.Encoding.UTF8.GetBytes("second"));
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", oldInput), ct);

        var result = await Jobs.UpdateJobInputAsync(enqueued, newInput, "reformat", "spec-actor", ct);
        Assert.Equal(JobControlAction.Applied, result.Action);

        var stored = await Jobs.GetInputAsync(enqueued, ct);
        Assert.NotNull(stored);
        Assert.Equal(JobPayloadFormat.Text.Id, stored!.Value.Format.Id);
        Assert.Equal(newInput.Data.ToArray(), stored.Value.Data.ToArray());
    }

    [Fact(DisplayName = "UpdateJobInput returns NotFound for an unknown lookup")]
    public async Task Amend_unknown_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await Jobs.UpdateJobInputAsync(JobLookup.ById(999_999_999_999L), JobPayload.Json(new AddNumbers(0, 0)), ct: ct);
        Assert.Equal(JobControlAction.NotFound, result.Action);
    }

    // ---------- helpers ----------

    private async Task<Job> ReadJobRowAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        return job!;
    }

    private Task SetJobStatusAsync(long jobId, byte statusCode, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );
}
