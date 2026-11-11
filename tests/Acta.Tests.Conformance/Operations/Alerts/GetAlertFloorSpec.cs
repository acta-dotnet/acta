using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>
/// Conformance for the alert floor retention and purge read before deleting: how far the namespace's
/// <c>sys.alerts</c> projector has consumed, and which skipped events it still owes a replay.
/// </summary>
[ConformanceSpec(
    "get-alert-floor.cursor-and-skips",
    "The alert floor is the projector's cursor and the skips it still holds",
    Area = "Alerts",
    Contract = "GetAlertFloor reads a namespace's sys.alerts cursor and skip records, by namespace or by one of its jobs, and a namespace without the slot owes nothing."
)]
public sealed class GetAlertFloorSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The projector's slot is what the floor reads, so the namespace has to carry it.
    protected override bool RunSystemJobs => true;

    [Fact(DisplayName = "A namespace without a sys.alerts slot owes nothing, so its floor is the purge cutoff")]
    public async Task Namespace_without_a_projector_owes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var bare = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(Unique("no-projector"), "test", ct);
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var floor = await Alerts.GetAlertFloorAsync(bare, ct);

        Assert.False(floor.SlotFound);
        Assert.Empty(floor.Poison);
        Assert.Null(floor.PoisonJson());
        Assert.Equal((cutoff, long.MaxValue), floor.Floor(cutoff));
    }

    [Fact(DisplayName = "A projector that has not checkpointed owes everything: its floor is the start of time")]
    public async Task Fresh_projector_owes_everything()
    {
        var ct = TestContext.Current.CancellationToken;

        var floor = await Alerts.GetAlertFloorAsync(NamespaceId, ct);

        Assert.True(floor.SlotFound);
        Assert.Equal((new DateTime(0L), 0L), floor.Floor(DateTime.UtcNow));
    }

    [Fact(DisplayName = "The floor is the cursor, and each skip record names its event and job, read by namespace or by a job in it")]
    public async Task Floor_reads_the_cursor_and_the_skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);

        await AlertTestOps.RewindAlertsCursorAsync(Services, TestNamespace, NamespaceId, projector, cursorEventId: 9001, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, NamespaceId, projector, eventId: 7001, ct, jobId: job.JobId);

        var byNamespace = await Alerts.GetAlertFloorAsync(NamespaceId, ct);
        Assert.True(byNamespace.SlotFound);
        Assert.Equal(9001, byNamespace.Cursor.EventId);
        Assert.Equal(new PoisonAlertEvent(7001, job.JobId, 0L), Assert.Single(byNamespace.Poison));
        Assert.Equal($"[{{\"event_id\":7001,\"job_id\":{job.JobId}}}]", byNamespace.PoisonJson());

        var byJob = await Services.GetRequiredService<GetAlertFloorHandler>().HandleAsync(new GetAlertFloorQuery(JobId: job.JobId), ct);
        Assert.Equal(byNamespace.Cursor, byJob.Cursor);
        Assert.Equal(byNamespace.Poison, byJob.Poison);
    }

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "An event whose job is gone leaves no skip record, and a record stored for one is dropped by the next pass")]
    public async Task A_gone_job_pins_no_floor()
    {
        var ct = TestContext.Current.CancellationToken;
        var projector = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "sys.alerts", ct);
        var seeder = new ActaTestSeeder(Db);
        var definitionId = await seeder.SeedJobDefinitionAsync(NamespaceId, Unique("gone-probe"), AlertProfileCode.OnTerminal, ct);
        var (jobId, _) = await seeder.SeedJobAsync(NamespaceId, definitionId, ct: ct);
        var recorded = await StageFailureAsync(definitionId, jobId, ct);
        var arriving = await StageFailureAsync(definitionId, jobId, ct);
        await AlertTestOps.RecordProjectionSkipAsync(Services, TestNamespace, NamespaceId, projector, recorded, ct, jobId: jobId);
        await Db.ExecuteRawAsync("DELETE FROM {schema}.jobs_state WHERE job_id = @p_id", ct, ("@p_id", jobId));
        await Db.ExecuteRawAsync("DELETE FROM {schema}.jobs WHERE job_id = @p_id", ct, ("@p_id", jobId));

        await AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, projector, options: null, drain: null, ct);

        // Nothing can bring the job back, so neither event is held: a record would keep the retention floor at it for good.
        var floor = await Alerts.GetAlertFloorAsync(NamespaceId, ct);
        Assert.Empty(floor.Poison);
        Assert.Equal(arriving, floor.Cursor.EventId);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    private Task<long> StageFailureAsync(int definitionId, long jobId, CancellationToken ct) =>
        StagedEvents.InsertAsync(
            Db,
            new EventEntity
            {
                EventCode = EventCode.JobFailed,
                NamespaceId = NamespaceId,
                EntityCode = EntityCode.Job,
                EntityId = jobId,
                DefinitionId = definitionId,
                ExecutionNumber = 1,
                EventMessage = "staged",
            },
            EventDetails.Write(("reason", EventReason.UnhandledException)),
            ct
        );

    private AlertsOperations Alerts => Services.GetRequiredService<AlertsOperations>();
}
