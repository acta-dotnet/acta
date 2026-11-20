using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Operator UpdateInputAsync: updates a job's stored input payload, compare-and-set on its version when one is given.
/// Allowed in any status except Executing (a mid-flight handler may already have read the input); the transition is
/// audited (job.input-updated) with bounded metadata (format name and byte count) about the
/// previous payload in the event detail, never the payload itself.
/// </summary>
[ConformanceSpec(
    "job.update-input",
    "Operator update-input updates stored input and audits bounded payload metadata.",
    Area = "Control",
    Contract = "UpdateJobInput replaces the input of a job that is not executing, compare-and-set on its version, and audits job.input-updated with prior-payload metadata."
)]
[Deploys("add-numbers", "job-wait-signal", "purge-now")]
public sealed class UpdateJobInputSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<UpdateJobInputSpec>>
{
    [Fact(DisplayName = "UpdateJobInput updates a Ready job's input and audits the old payload's format and byte count")]
    public async Task Update_ready_job_applies_new_input_and_audits_old_payload()
    {
        var ct = TestContext.Current.CancellationToken;

        var oldInput = JobPayload.Json(new AddNumbers(2, 3));
        var newInput = JobPayload.Json(new AddNumbers(7, 8));
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", oldInput), ct);

        var result = await Jobs.UpdateInputAsync(enqueued, newInput, "corrected operands", "spec-actor", ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);

        var job = await ReadJobRowAsync(enqueued.JobId, ct);
        Assert.Equal(newInput.Format.Id, job.InputFormatId);
        Assert.Equal(newInput.Data.ToArray(), job.Input);

        var evt = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobInputUpdated, ct);
        Assert.Equal("spec-actor", evt.Actor);
        Assert.Null(EventDetail(evt, "actor"));
        Assert.Equal("corrected operands", evt.EventMessage);
        Assert.Equal(JobPayloadFormat.Json.Id, evt.DetailsFormatId);
        Assert.NotNull(evt.Details);
        using var doc = System.Text.Json.JsonDocument.Parse(evt.Details!);
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(oldInput.Data.Length, doc.RootElement.GetProperty("bytes").GetInt32());
        // The old payload bytes themselves must be gone from the ledger.
        Assert.True(evt.Details!.AsSpan().IndexOf(oldInput.Data.Span) < 0);
    }

    // ACTA-004 acceptance: the updated-away payload must not outlive the job's payload retention.
    private const int NoPurgeDays = 100_000;

    [Fact(DisplayName = "UpdateJobInput audit metadata outlives the purged job without leaking payload bytes")]
    public async Task Updated_old_payload_never_survives_the_retention_purge()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var marker = $"acta004-{Guid.NewGuid():N}";

        // Terminal Succeeded through the real path via the zero-retention probe, carrying the marker input.
        var oldInput = JobPayload.Json(new PurgeProbe(marker));
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "purge-now", oldInput), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var update = await Jobs.UpdateInputAsync(enqueued, JobPayload.Json(new PurgeProbe("clean")), "scrub", "spec-actor", ct: ct);
        Assert.Equal(ControlAction.Applied, update.Action);

        // Sweep with the retention window wide open: the job row goes, the ledger stays.
        Task<JobEntity?> RowAsync() => Db.From<JobEntity>().Where(j => j.Id == enqueued.JobId).SingleOrDefaultAsync(ct);
        await RetentionTestOps.PurgeUntilAsync(Services, ns, NoPurgeDays, 1000, 50, async () => await RowAsync() is null, ct);
        Assert.Null(await RowAsync());

        // The update event outlives the job (events carry no FK to jobs) with metadata only.
        var evt = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobInputUpdated, ct);
        Assert.Equal(JobPayloadFormat.Json.Id, evt.DetailsFormatId);
        using var doc = System.Text.Json.JsonDocument.Parse(evt.Details!);
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(oldInput.Data.Length, doc.RootElement.GetProperty("bytes").GetInt32());

        // No surviving event row or API projection carries the marker anywhere.
        var rows = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == enqueued.JobId).ToListAsync(ct);
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.DoesNotContain(marker, row.EventMessage ?? "");
            if (row.Details is not null)
            {
                Assert.DoesNotContain(marker, System.Text.Encoding.UTF8.GetString(row.Details));
            }
        }

        var page = await Operations.Ledger.ListEventsAsync(new ListEventsQuery(Job: JobLookup.ById(enqueued.JobId), PageSize: 100), ct);
        Assert.Contains(page.Items, i => i.EventCode == EventCode.JobInputUpdated && i.DetailsText is not null);
        foreach (var item in page.Items)
        {
            Assert.DoesNotContain(marker, item.DetailsText ?? "");
            Assert.DoesNotContain(marker, item.Message ?? "");
        }
    }

    [Fact(DisplayName = "UpdateJobInput rejects an Executing job and leaves its input unchanged")]
    public async Task Update_in_flight_job_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var newInput = JobPayload.Json(new AddNumbers(9, 9));
        var oldInput = JobPayload.Json(new AddNumbers(1, 1));
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", oldInput), ct);
        await SetJobStatusAsync(enqueued.JobId, (byte)JobStatusCode.Executing, ct);

        var result = await Jobs.UpdateInputAsync(enqueued, newInput, "should not apply", ct: ct);

        Assert.Equal(ControlAction.Rejected, result.Action);
        Assert.Equal(JobStatusCode.Executing, result.Status);

        var job = await ReadJobRowAsync(enqueued.JobId, ct);
        Assert.Equal(oldInput.Data.ToArray(), job.Input);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobInputUpdated, ct));
    }

    [Fact(DisplayName = "UpdateJobInput on a Failed job feeds the new input to the handler after RestartAsync")]
    public async Task Update_failed_job_then_restart_runs_with_new_input()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        await SetJobStatusAsync(enqueued.JobId, (byte)JobStatusCode.Failed, ct);

        var update = await Jobs.UpdateInputAsync(enqueued, JobPayload.Json(new AddNumbers(10, 20)), "retry with new operands", ct: ct);
        Assert.Equal(ControlAction.Applied, update.Action);

        var restart = await Jobs.RestartAsync(enqueued, ct: ct);
        Assert.Equal(ControlAction.Applied, restart.Action);
        Assert.Equal(JobStatusCode.Ready, restart.Status);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued.JobId, ct));

        var typed = await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct);
        Assert.Equal(30, typed.Sum);
    }

    [Fact(DisplayName = "UpdateJobInput stores the new payload's format id, so a bytes job updates as bytes")]
    public async Task Update_stores_the_payloads_format_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var oldInput = JobPayload.Bytes([1, 2]);
        var newInput = JobPayload.Bytes([3, 4, 5]);
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", oldInput), ct);

        var result = await Jobs.UpdateInputAsync(enqueued, newInput, "reformat", "spec-actor", ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);

        var stored = (await Jobs.GetInputAsync(enqueued, ct))?.Input;
        Assert.NotNull(stored);
        Assert.Equal(JobPayloadFormat.Bytes.Id, stored!.Value.Format.Id);
        Assert.Equal(newInput.Data.ToArray(), stored.Value.Data.ToArray());
    }

    [Fact(DisplayName = "UpdateJobInput with a stale expected version is a version conflict, and an applied update moves the version")]
    public async Task Update_is_compare_and_set_on_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var version = (await Jobs.GetAsync(enqueued, ct))!.Version;

        var stale = await Jobs.UpdateInputAsync(enqueued, JobPayload.Json(new AddNumbers(4, 4)), expectedVersion: version + 1, ct: ct);
        Assert.Equal(ControlAction.VersionConflict, stale.Action);
        Assert.Equal(version, stale.Version);

        var updated = JobPayload.Json(new AddNumbers(5, 5));
        var applied = await Jobs.UpdateInputAsync(enqueued, updated, expectedVersion: version, ct: ct);
        Assert.Equal(ControlAction.Applied, applied.Action);
        Assert.Equal(version + 1, applied.Version);
        Assert.Equal(version + 1, (await Jobs.GetAsync(enqueued, ct))!.Version);
        Assert.Equal(updated.Data.ToArray(), (await ReadJobRowAsync(enqueued.JobId, ct)).Input);
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobInputUpdated, ct));
    }

    [Fact(DisplayName = "UpdateJobInput rejects a job with no input to replace and writes nothing")]
    public async Task Update_rejects_a_job_with_no_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        var result = await Jobs.UpdateInputAsync(enqueued, JobPayload.Json(new AddNumbers(1, 1)), ct: ct);

        Assert.Equal(ControlAction.Rejected, result.Action);
        Assert.Equal(JobPayloadFormat.None.Id, (await ReadJobRowAsync(enqueued.JobId, ct)).InputFormatId);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobInputUpdated, ct));
    }

    [Fact(DisplayName = "UpdateJobInput returns NotFound for an unknown lookup")]
    public async Task Update_unknown_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await Jobs.UpdateInputAsync(JobLookup.ById(999_999_999_999L), JobPayload.Json(new AddNumbers(0, 0)), ct: ct);
        Assert.Equal(ControlAction.NotFound, result.Action);
    }

    private async Task<JobEntity> ReadJobRowAsync(long jobId, CancellationToken ct)
    {
        var job = await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
        return job!;
    }

    private Task SetJobStatusAsync(long jobId, byte statusCode, CancellationToken ct) =>
        JobStateStaging.SetStatusAsync(Db, jobId, statusCode, ct);
}
