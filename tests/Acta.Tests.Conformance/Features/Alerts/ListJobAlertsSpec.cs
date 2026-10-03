using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the alerts list read: alert rows newest first with severity-floor and
/// unresolved-only filters, full stored text, and an opt-in total.
/// </summary>
[ConformanceSpec(
    "list-job-alerts.keyset-page",
    "ListJobAlerts pages alerts newest first with severity floor and full stored text",
    Area = "Reads",
    Contract = "ListJobAlerts returns alert rows ordered created_at_utc then id descending with severity floor and an opt-in filter-wide count in one command."
)]
public sealed class ListJobAlertsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Alerts page newest first with the severity floor excluding lower rows, full stored text, and a filter-wide total")]
    public async Task Lists_alerts_newest_first_with_severity_floor_and_full_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var longTitle = new string('x', 200);
        foreach (var severity in new[] { AlertSeverityCode.Info, AlertSeverityCode.Error, AlertSeverityCode.Critical })
        {
            await AlertTestOps.RaiseAsync(
                Services,
                TestNamespace,
                null,
                AlertOriginCode.Manual,
                severity,
                AlertKindCode.Manual,
                longTitle,
                "list-spec message",
                "default",
                AlertDeliveryStatusCode.Pending,
                null,
                ct
            );
        }

        var all = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, IncludeTotal: true), ct);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(3, all.Items.Count);
        Assert.All(all.Items, a => Assert.Equal(longTitle, a.Title));
        for (var i = 1; i < all.Items.Count; i++)
        {
            var ordered =
                all.Items[i].CreatedAtUtc < all.Items[i - 1].CreatedAtUtc
                || (all.Items[i].CreatedAtUtc == all.Items[i - 1].CreatedAtUtc && all.Items[i].AlertId < all.Items[i - 1].AlertId);
            Assert.True(ordered, "rows are not in created_at DESC, id DESC order");
        }

        var floored = await queries.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, SeverityAtLeast: AlertSeverityCode.Error),
            ct
        );
        Assert.Equal(2, floored.Items.Count);
        Assert.All(floored.Items, static a => Assert.True(a.Severity >= AlertSeverityCode.Error));

        var unresolved = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, UnresolvedOnly: true), ct);
        Assert.Equal(3, unresolved.Items.Count);
    }

    [Fact(DisplayName = "Alert list keeps the job ref after the job row is gone")]
    public async Task Alert_list_keeps_job_ref_after_job_purge()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var completed = await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct);
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            completed.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "job-ref-survives-purge alert",
            "list-spec message",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        // Direct DB delete (not the operator purge verb): jobs has no FK to alerts, so this proves the
        // list read itself keeps job_ref from the alert's own column, not by joining back to jobs.
        await Db.ExecuteRawAsync("DELETE FROM {schema}.jobs WHERE id = @p_id", ct, ("@p_id", completed.JobId));

        var page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct);
        var alert = Assert.Single(page.Items);
        Assert.Equal(completed.JobRef, alert.JobRef);
    }

    [Fact(DisplayName = "ListJobAlerts returns the keyset page and the filter-wide total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;

        var longTitle = new string('x', 200);
        foreach (var severity in new[] { AlertSeverityCode.Info, AlertSeverityCode.Error, AlertSeverityCode.Critical })
        {
            await AlertTestOps.RaiseAsync(
                Services,
                TestNamespace,
                null,
                AlertOriginCode.Manual,
                severity,
                AlertKindCode.Manual,
                longTitle,
                "list-spec message",
                "default",
                AlertDeliveryStatusCode.Pending,
                null,
                ct
            );
        }

        var page = await Services
            .GetRequiredService<IAlerts>()
            .ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, PageSize: 2, IncludeTotal: true), ct);
        var (rows, total) = (page.Items, page.TotalCount);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3L, total);
        for (var i = 1; i < rows.Count; i++)
        {
            var earlier = rows[i - 1];
            var current = rows[i];
            Assert.True(
                current.CreatedAtUtc < earlier.CreatedAtUtc
                    || (current.CreatedAtUtc == earlier.CreatedAtUtc && current.AlertId < earlier.AlertId),
                "combined page rows are not in created_at_utc DESC, id DESC order"
            );
        }
    }

    [Fact(DisplayName = "An acknowledged alert row carries acknowledged_at_utc, an open one carries null")]
    public async Task Acknowledged_alert_projects_the_stamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "ack-me",
            "acknowledge test",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "leave-open",
            "acknowledge test",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        var alerts = await Db.From<JobAlert>().OrderByDescending(a => a.Id).ToListAsync(ct);
        var acked = alerts.First(a => a.Title == "ack-me");
        var ackedRef = new AlertRef(acked.AlertRef);

        await queries.Alerts.AcknowledgeAsync(ackedRef, actorKey: "op", ct: ct);

        var page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, PageSize: 50), ct);
        var ackedItem = page.Items.Single(a => a.AlertRef == ackedRef);
        Assert.NotNull(ackedItem.AcknowledgedAtUtc);
        Assert.Contains(page.Items, a => a.AcknowledgedAtUtc is null);
    }

    [Fact(DisplayName = "GetAsync point-reads one alert in the list projection shape and answers null for a missing id")]
    public async Task Get_point_reads_one_alert_and_null_for_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            "get-spec title",
            "get-spec message",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        var listed = Assert.Single(
            (await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct)).Items,
            a => a.Title == "get-spec title"
        );

        var detail = await queries.Alerts.GetAsync(listed.AlertRef, ct);
        Assert.NotNull(detail);
        Assert.Equal(listed.AlertRef, detail.AlertRef);
        Assert.Equal(listed.JobNamespace, detail.JobNamespace);
        Assert.Equal("get-spec message", detail.Message);
        Assert.Equal(listed.CreatedAtUtc, detail.CreatedAtUtc);

        Assert.Null(await queries.Alerts.GetAsync(AlertRef.New(), ct));
    }
}
