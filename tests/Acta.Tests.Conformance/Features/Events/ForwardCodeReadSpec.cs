using System.Text.Json;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Events;

/// <summary>
/// Conformance for reading forward: a row a newer Acta wrote, carrying a code id this build does not
/// know, reads back as the extensible family's Unspecified member and serializes, instead of
/// surfacing an undefined enum value that throws when named.
/// </summary>
[ConformanceSpec(
    "persisted-codes.forward-read",
    "An event or alert code from a newer Acta reads back as unspecified",
    Area = "Reads",
    Contract = "An unassigned id in events.event_code, events.reason_code, or alerts.kind_code reads back as Unspecified on every read path and serializes as \"unspecified\".",
    Arrange = "A job's timeline and one alert are written, then their extensible code columns are overwritten with an id no member holds.",
    Act = "The events and the alert are read through IActaOperations and the table query surface, then serialized with web JSON defaults.",
    Assert = "Every read decodes to Unspecified without throwing, and the JSON names the code \"unspecified\"."
)]
[CoversStoreMethod(typeof(IEventStore), nameof(IEventStore.ListEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ListJobAlertsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetJobAlertAsync))]
public abstract class ForwardCodeReadSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // No member of EventCode, JobEventReasonCode, or AlertKindCode holds this id, and none of the three
    // columns carries an IN-list CHECK, so a newer Acta may write it.
    private const byte UnassignedId = 200;

    // The dashboard API serializes camelCase through each code family's own converter.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact(DisplayName = "An unassigned event and reason code read back as Unspecified and serialize as unspecified")]
    public async Task Unassigned_event_codes_read_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var jobRef = enqueued.JobRef.Value;

        var rewritten = await Db.From<JobEvent>()
            .Where(e => e.JobRef == jobRef)
            .UpdateOnlyAsync(() => new JobEvent { EventCode = (EventCode)UnassignedId, ReasonCode = (JobEventReasonCode)UnassignedId }, ct);
        Assert.True(rewritten > 0);

        var page = await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: enqueued.JobId, PageSize: 100), ct);
        Assert.Equal(rewritten, page.Items.Count);
        Assert.All(
            page.Items,
            e =>
            {
                Assert.Equal(EventCode.Unspecified, e.EventCode);
                Assert.Equal(JobEventReasonCode.Unspecified, e.ReasonCode);
            }
        );

        var json = JsonSerializer.Serialize(page, WebJson);
        Assert.Contains("\"eventCode\":\"unspecified\"", json);
        Assert.Contains("\"reasonCode\":\"unspecified\"", json);

        var rows = await Db.From<JobEvent>().Where(e => e.JobRef == jobRef).ToListAsync(ct);
        Assert.All(
            rows,
            e =>
            {
                Assert.Equal(EventCode.Unspecified, e.EventCode);
                Assert.Equal(JobEventReasonCode.Unspecified, e.ReasonCode);
            }
        );
    }

    [Fact(DisplayName = "An unassigned alert kind reads back as Unspecified from list and get and serializes as unspecified")]
    public async Task Unassigned_alert_kind_reads_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
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
        var rewritten = await Db.From<JobAlert>()
            .Where(a => a.JobRef == jobRef)
            .UpdateOnlyAsync(() => new JobAlert { Kind = (AlertKindCode)UnassignedId }, ct);
        Assert.Equal(1, rewritten);

        var list = await Operations.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct);
        var item = Assert.Single(list.Items, a => a.JobRef == enqueued.JobRef);
        Assert.Equal(AlertKindCode.Unspecified, item.Kind);
        Assert.Contains("\"kind\":\"unspecified\"", JsonSerializer.Serialize(list, WebJson));

        var detail = await Operations.Alerts.GetAsync(item.AlertRef, ct);
        Assert.NotNull(detail);
        Assert.Equal(AlertKindCode.Unspecified, detail!.Kind);
        Assert.Contains("\"kind\":\"unspecified\"", JsonSerializer.Serialize(detail, WebJson));

        var row = await Db.From<JobAlert>().Where(a => a.JobRef == jobRef).SingleOrDefaultAsync(ct);
        Assert.Equal(AlertKindCode.Unspecified, row!.Kind);
    }
}
