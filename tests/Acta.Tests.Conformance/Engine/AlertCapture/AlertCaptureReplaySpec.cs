using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for poison recovery: an event whose projection fails deterministically is recorded and passed by the
/// cursor, kept with its job by retention, and replayed at the start of every pass until it projects, or until a newer
/// success of its job has answered it, which disposes of it without an incident.
/// </summary>
[ConformanceSpec(
    "alert-capture.poison-replay",
    "A skipped alert event is kept and replayed; a newer success disposes of it",
    Area = "Alerts",
    Contract = "A poison alert event stays protected with its job after the cursor passes it, projects once fixed, and a newer success supersedes it."
)]
public sealed class AlertCaptureReplaySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // A channel name canonicalization rejects, set on the definition after its job failed.
    private const string BrokenChannel = "Broken Channel!";

    // The floor and the skip records live on the namespace's own sys.alerts slot.
    protected override bool RegisterSystemJobs => true;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A failure skipped as poison is kept by retention past the cursor and raises its alert once the definition is fixed"
    )]
    public async Task Skipped_failure_is_kept_then_replayed()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertCaptureOps.ProjectorAsync(Services, TestNamespace, ct);
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        var failed = Assert.Single(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.EventCode == EventCode.JobFailed);

        // The raise fails canonicalization: the event is recorded as a skip and the cursor moves past it.
        await AlertCaptureOps.OverrideChannelAsync(Db, job.JobId, BrokenChannel, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Empty(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct));
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));
        Assert.Equal(failed.Id, await AlertTestOps.ReadAlertsCursorAsync(Services, TestNamespace, NamespaceId, projector, ct));
        await Assert.ThrowsAsync<JobPurgeBlockedException>(async () => await Jobs.PurgeAsync(job, "spec-actor", ct));

        // Behind the cursor but still owed: the sweep keeps the event and its job.
        var aged = failed.CreatedAtUtc;
        var skipName = AlertTestOps.SkipVariableName(failed.Id);
        await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == projector && c.CheckpointName == skipName)
            .UpdateOnlyAsync(() => new CheckpointEntity { ModifiedAtUtc = aged, CreatedAtUtc = aged }, ct);
        await RetentionTestOps.PurgeAsync(Services, NamespaceId, AlertCaptureOps.EventsRetentionDays, 30, 100_000_000, 1000, 50, ct);
        Assert.NotNull(await Jobs.GetAsync(job, ct));
        Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.Id == failed.Id);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));

        // Fixed, the next pass replays it before reading on, and the record goes.
        await AlertCaptureOps.OverrideChannelAsync(Db, job.JobId, null, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Equal(AlertKindCode.FinalFailure, Assert.Single(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct)).Kind);
        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));

        await AlertCaptureOps.SweepUntilGoneAsync(Services, Db, NamespaceId, job.JobId, ct);
        Assert.Null(await Jobs.GetAsync(job, ct));
    }

    [Fact(DisplayName = "A skipped failure whose job then recovered is disposed of on replay without opening an incident")]
    public async Task Superseded_skip_opens_no_incident()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertCaptureOps.ProjectorAsync(Services, TestNamespace, ct);
        FlakyRecoverProbe.Reset(TestNamespace, failingAttempts: 1);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FlakyRecoverProbe.Attempts(TestNamespace), 2, ct);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(job, ct))?.Status);
        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        var events = await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct);
        var failed = Assert.Single(events, e => e.EventCode == EventCode.JobExecutionFailed);
        var success = Assert.Single(events, e => e.EventCode == EventCode.JobExecutionSucceeded);

        // One pass: the failure is skipped as poison, and the recovery after it has nothing open to resolve.
        await AlertCaptureOps.OverrideChannelAsync(Db, job.JobId, BrokenChannel, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));
        Assert.Equal(success.Id, await AlertTestOps.ReadAlertsCursorAsync(Services, TestNamespace, NamespaceId, projector, ct));

        await AlertCaptureOps.SweepAsync(Services, NamespaceId, ct);
        Assert.NotNull(await Jobs.GetAsync(job, ct));
        Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.Id == success.Id);

        // Replayed after the fix, the failure is already answered: no incident opens, open or resolved, and the
        // record goes.
        await AlertCaptureOps.OverrideChannelAsync(Db, job.JobId, null, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Empty(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct));
        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));
    }

    [Fact(DisplayName = "All unresolved skips and their jobs survive retention and replay in event position order")]
    public async Task Backlog_beyond_the_former_cap_is_kept_and_replayed_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertCaptureOps.ProjectorAsync(Services, TestNamespace, ct);
        List<(EnqueueJobResponse Job, long EventId, DateTime Stamp)> skipped = [];
        var oldest = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 150; i++)
        {
            var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
            await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
            var failed = Assert.Single(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.EventCode == EventCode.JobFailed);
            var stamp = oldest.AddMilliseconds((149 - i) / 2);
            await Db.From<EventEntity>()
                .Where(e => e.WorkerId == failed.WorkerId && e.Id == failed.Id)
                .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = stamp }, ct);
            skipped.Add((job, failed.Id, stamp));
        }

        await AlertCaptureOps.OverrideChannelAsync(Db, skipped[0].Job.JobId, BrokenChannel, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        var alerts = Services.GetRequiredService<AlertsOperations>();
        var floor = await alerts.GetAlertFloorAsync(NamespaceId, ct);
        Assert.Equal(150, floor.Poison.Count);
        var ordered = skipped.OrderBy(s => s.Stamp).ThenBy(s => s.EventId).Select(s => s.EventId).ToArray();
        Assert.Equal(ordered, (await alerts.GetPoisonAlertEventsAsync(NamespaceId, floor.PoisonJson()!, ct)).Events.Select(e => e.EventId));

        await AlertCaptureOps.SweepAsync(Services, NamespaceId, ct);
        foreach (var skip in skipped)
        {
            Assert.NotNull(await Jobs.GetAsync(skip.Job, ct));
            Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, skip.Job.JobId, ct), e => e.Id == skip.EventId);
        }
        Assert.Equal(150, (await alerts.GetAlertFloorAsync(NamespaceId, ct)).Poison.Count);

        await AlertCaptureOps.OverrideChannelAsync(Db, skipped[0].Job.JobId, null, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Empty((await alerts.GetAlertFloorAsync(NamespaceId, ct)).Poison);
        var projected = await Db.From<AlertEntity>().Where(a => a.NamespaceId == NamespaceId).ToListAsync(ct);
        Assert.Equal(ordered, projected.OrderBy(a => a.Id).Select(a => a.LastProjectedEventId!.Value));
    }

    [Fact(DisplayName = "Disabling alerts disposes of a skip without decoding its details and releases retention")]
    public async Task Disabled_profile_drops_skip_without_decoding_details()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertCaptureOps.ProjectorAsync(Services, TestNamespace, ct);
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        var failed = Assert.Single(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.EventCode == EventCode.JobFailed);
        await AlertCaptureOps.OverrideChannelAsync(Db, job.JobId, BrokenChannel, ct);
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Equal(1, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));

        await Db.From<EventEntity>()
            .Where(e => e.WorkerId == failed.WorkerId && e.Id == failed.Id)
            .UpdateOnlyAsync(() => new EventEntity { DetailsFormatId = JobPayloadFormat.Json.Id, Details = new byte[] { 0xff } }, ct);
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == failed.DefinitionId)
            .UpdateOnlyAsync(() => new DefinitionEntity { AlertProfileOverride = AlertProfileCode.None }, ct);

        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Equal(0, await CountVariableAsync(projector, AlertTestOps.SkipVariableName(failed.Id), ct));
        Assert.Empty(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct));
        await AlertCaptureOps.SweepUntilGoneAsync(Services, Db, NamespaceId, job.JobId, ct);
        Assert.Null(await Jobs.GetAsync(job, ct));
        Assert.Empty(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct));
    }
}
