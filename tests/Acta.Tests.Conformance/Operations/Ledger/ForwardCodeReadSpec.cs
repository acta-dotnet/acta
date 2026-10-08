using System.Text.Json;
using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Ledger;

/// <summary>
/// Conformance for reading forward: a row a newer Acta wrote, carrying a code id this build does not
/// know, reads back as the family's id-0 member and serializes, instead of
/// surfacing an undefined enum value that throws when named.
/// </summary>
[ConformanceSpec(
    "persisted-codes.forward-read",
    "An event or alert code from a newer Acta reads back as unspecified",
    Area = "Reads",
    Contract = "An unassigned id in events.event_code, events.reason_code, or alerts.kind_code reads back as Unspecified on every read path and serializes as \"unspecified\"."
)]
public sealed class ForwardCodeReadSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // No member of EventCode, EventReasonCode, or AlertKindCode holds this id, and none of the three
    // columns carries an IN-list CHECK, so a newer Acta may write it.
    private const byte UnassignedId = 200;

    // The dashboard API serializes camelCase through each code family's own converter.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "An unassigned event and reason code read back as Unspecified and serialize as unspecified")]
    public async Task Unassigned_event_codes_read_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var jobRef = enqueued.JobRef.Value;

        var rewritten = await Db.From<EventEntity>()
            .Where(e => e.JobRef == jobRef)
            .UpdateOnlyAsync(() => new EventEntity { EventCode = (EventCode)UnassignedId, ReasonCode = (EventReasonCode)UnassignedId }, ct);
        Assert.True(rewritten > 0);

        var page = await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: enqueued.JobId, PageSize: 100), ct);
        Assert.Equal(rewritten, page.Items.Count);
        Assert.All(
            page.Items,
            e =>
            {
                Assert.Equal(EventCode.Unspecified, e.EventCode);
                Assert.Equal(EventReasonCode.Unspecified, e.ReasonCode);
            }
        );

        var json = JsonSerializer.Serialize(page, WebJson);
        Assert.Contains("\"eventCode\":\"unspecified\"", json);
        Assert.Contains("\"reasonCode\":\"unspecified\"", json);

        var rows = await Db.From<EventEntity>().Where(e => e.JobRef == jobRef).ToListAsync(ct);
        Assert.All(
            rows,
            e =>
            {
                Assert.Equal(EventCode.Unspecified, e.EventCode);
                Assert.Equal(EventReasonCode.Unspecified, e.ReasonCode);
            }
        );
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
