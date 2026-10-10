using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the point read on IAlerts: one alert by its public ref, as the row stands at the moment of the read.</summary>
[ConformanceSpec(
    "get-job-alert.by-ref",
    "GetAsync reads one alert by its public ref as it stands now",
    Area = "Reads",
    Contract = "Alerts.GetAsync returns the alert row its ref names, as it stands, with the namespace name and job ref, and null for a ref no alert carries."
)]
public sealed class GetAlertSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "GetAsync answers the stored row under its ref with the namespace name and the job's public ref")]
    public async Task Get_answers_the_stored_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var row = await RaiseAsync(job.JobId, deduplicationKey: null, ct);

        var detail = await Operations.Alerts.GetAsync(new AlertRef(row.AlertRef), ct);

        Assert.NotNull(detail);
        Assert.Equal(TestNamespace, detail.JobNamespace);
        Assert.Equal(job.JobRef, detail.JobRef);
        Assert.Equal("get-spec alert", detail.Title);
        Assert.Equal(AlertSeverityCode.Warning, detail.Severity);
        AssertMatchesRow(row, detail);
    }

    [Fact(DisplayName = "GetAsync answers null for a ref no alert carries")]
    public async Task Get_answers_null_for_an_unknown_ref()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await Operations.Alerts.GetAsync(AlertRef.New(), ct));
    }

    [Fact(DisplayName = "GetAsync reflects the row after a delivery settle, an acknowledge, and a resolve")]
    public async Task Get_reflects_the_current_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var raised = await RaiseAsync(job.JobId, deduplicationKey: null, ct);
        var alertRef = new AlertRef(raised.AlertRef);
        var retryAfter = new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(
            (
                await Services
                    .GetRequiredService<AlertsOperations>()
                    .UpdateAlertDeliveryAsync(raised.Id, raised.Version, AlertDeliveryStatusCode.RetryAfter, 3, retryAfter, ct)
            ).Updated
        );
        var settled = await Operations.Alerts.GetAsync(alertRef, ct);
        Assert.NotNull(settled);
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, settled.DeliveryStatus);
        Assert.Equal(3, settled.RetryCount);
        Assert.Equal(retryAfter, settled.RetryAfterUtc);

        var acknowledged = await Operations.Alerts.AcknowledgeAsync(alertRef, ct: ct);
        Assert.Equal(acknowledged.AcknowledgedAtUtc, (await Operations.Alerts.GetAsync(alertRef, ct))!.AcknowledgedAtUtc);

        var resolved = await Operations.Alerts.ResolveAsync(alertRef, ct: ct);
        var detail = await Operations.Alerts.GetAsync(alertRef, ct);
        Assert.NotNull(detail);
        Assert.Equal(resolved.ResolvedAtUtc, detail.ResolvedAtUtc);
        AssertMatchesRow(await RowAsync(raised.Id, ct), detail);
    }

    [Fact(DisplayName = "A job-less alert reads with no job id and no job ref")]
    public async Task Jobless_alert_reads_without_a_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = await RaiseAsync(jobId: null, deduplicationKey: null, ct);

        var detail = await Operations.Alerts.GetAsync(new AlertRef(row.AlertRef), ct);

        Assert.NotNull(detail);
        Assert.Null(detail.JobId);
        Assert.Null(detail.JobRef);
        AssertMatchesRow(row, detail);
    }

    [Fact(
        DisplayName = "A resolved incident's ref keeps answering its closed row after its key opens a fresh incident with a ref of its own"
    )]
    public async Task Resolved_ref_keeps_answering_its_closed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var key = TestKey("incident");
        var firstRow = await RaiseAsync(job.JobId, key, ct);
        var first = new AlertRef(firstRow.AlertRef);
        Assert.Equal(ControlAction.Applied, (await Operations.Alerts.ResolveAsync(first, ct: ct)).Action);

        var secondRow = await RaiseAsync(job.JobId, key, ct);
        Assert.NotEqual(firstRow.AlertRef, secondRow.AlertRef);

        var closed = await Operations.Alerts.GetAsync(first, ct);
        Assert.NotNull(closed);
        Assert.Equal(firstRow.Id, closed.AlertId);
        Assert.NotNull(closed.ResolvedAtUtc);

        var fresh = await Operations.Alerts.GetAsync(new AlertRef(secondRow.AlertRef), ct);
        Assert.NotNull(fresh);
        Assert.Equal(secondRow.Id, fresh.AlertId);
        Assert.Null(fresh.ResolvedAtUtc);
        Assert.Equal(1, fresh.OccurrenceCount);
    }

    private static void AssertMatchesRow(AlertEntity row, GetAlertResponse detail)
    {
        Assert.Equal(row.AlertRef, detail.AlertRef.Value);
        Assert.Equal(row.Id, detail.AlertId);
        Assert.Equal(row.JobId, detail.JobId);
        Assert.Equal(row.JobRef, detail.JobRef?.Value);
        Assert.Equal(row.OriginCode, detail.Origin);
        Assert.Equal(row.SeverityCode, detail.Severity);
        Assert.Equal(row.Kind, detail.Kind);
        Assert.Equal(row.Title, detail.Title);
        Assert.Equal(row.Message, detail.Message);
        Assert.Equal(row.ChannelName, detail.ChannelName);
        Assert.Equal(row.OccurrenceCount, detail.OccurrenceCount);
        Assert.Equal(row.ResolvedAtUtc, detail.ResolvedAtUtc);
        Assert.Equal(row.DeliveryStatusCode, detail.DeliveryStatus);
        Assert.Equal(row.RetryCount, detail.RetryCount);
        Assert.Equal(row.RetryAfterUtc, detail.RetryAfterUtc);
        Assert.Equal(row.CreatedAtUtc, detail.CreatedAtUtc);
        Assert.Equal(row.ModifiedAtUtc, detail.ModifiedAtUtc);
        Assert.Equal(row.AcknowledgedAtUtc, detail.AcknowledgedAtUtc);
    }

    private ValueTask<EnqueueJobResponse> EnqueueAsync(CancellationToken ct) =>
        Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

    private async Task<AlertEntity> RowAsync(long alertId, CancellationToken ct) =>
        Assert.Single(await Db.From<AlertEntity>().Where(a => a.Id == alertId).ToListAsync(ct));

    /// <summary>Raises one manual alert and returns the row it opened or repeated, the newest of the namespace.</summary>
    private async Task<AlertEntity> RaiseAsync(long? jobId, string? deduplicationKey, CancellationToken ct)
    {
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "get-spec alert",
            "get-spec message",
            "default",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            ct
        );
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        return (await ReadAlertsAsync(namespaceId, ct)).OrderByDescending(a => a.Id).First();
    }
}
