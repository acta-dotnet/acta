using System.Text.Json;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Ledger;

/// <summary>
/// Conformance for reading forward: a row a newer Acta wrote, carrying a code id this build does not
/// know, reads back as the family's id-0 member and serializes, instead of
/// surfacing an undefined enum value that throws when named. The operator views need no fallback, and an
/// unknown id decodes there to NULL beside its raw code column: the newest build's migration ships the newest
/// views, so a view knows every code a running build writes.
/// </summary>
[ConformanceSpec(
    "persisted-codes.forward-read",
    "An event or alert code from a newer Acta reads back as unspecified",
    Area = "Reads",
    Contract = "An unassigned event code or alert kind id reads back as Unspecified on every API read path and serializes as \"unspecified\"."
)]
public sealed class ForwardCodeReadSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // No member of EventCode or AlertKindCode holds this id, and neither column carries an IN-list
    // CHECK, so a newer Acta may write it.
    private const byte UnassignedId = 200;

    // The dashboard API serializes camelCase through each code family's own converter.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "An unassigned event code reads back as Unspecified and serializes as unspecified")]
    public async Task Unassigned_event_codes_read_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var jobId = enqueued.JobId;

        var rewritten = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId)
            .UpdateOnlyAsync(() => new EventEntity { EventCode = (EventCode)UnassignedId }, ct);
        Assert.True(rewritten > 0);

        var page = await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 100), ct);
        Assert.Equal(rewritten, page.Items.Count);
        Assert.All(page.Items, e => Assert.Equal(EventCode.Unspecified, e.EventCode));
        Assert.Contains("\"eventCode\":\"unspecified\"", JsonSerializer.Serialize(page, WebJson));

        var rows = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct);
        Assert.All(rows, e => Assert.Equal(EventCode.Unspecified, e.EventCode));
    }

    [Fact(DisplayName = "An unassigned alert kind reads back as Unspecified from list and get and serializes as unspecified")]
    public async Task Unassigned_alert_kind_reads_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            enqueued.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "forward-read alert",
            "forward-read message",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        var jobRef = enqueued.JobRef.Value;
        var rewritten = await Db.From<AlertEntity>()
            .Where(a => a.JobRef == jobRef)
            .UpdateOnlyAsync(() => new AlertEntity { Kind = (AlertKindCode)UnassignedId }, ct);
        Assert.Equal(1, rewritten);

        var list = await Operations.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct);
        var item = Assert.Single(list.Items, a => a.JobRef == enqueued.JobRef);
        Assert.Equal(AlertKindCode.Unspecified, item.Kind);
        Assert.Contains("\"kind\":\"unspecified\"", JsonSerializer.Serialize(list, WebJson));

        var detail = await Operations.Alerts.GetAsync(item.AlertRef, ct);
        Assert.NotNull(detail);
        Assert.Equal(AlertKindCode.Unspecified, detail!.Kind);
        Assert.Contains("\"kind\":\"unspecified\"", JsonSerializer.Serialize(detail, WebJson));

        var row = await Db.From<AlertEntity>().Where(a => a.JobRef == jobRef).SingleOrDefaultAsync(ct);
        Assert.Equal(AlertKindCode.Unspecified, row!.Kind);
    }
}
