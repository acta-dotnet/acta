using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for <c>ListJobAlerts</c> filter dimensions: each filter partitions the alert result
/// set to exactly the matching rows and the opt-in total count applies the same filter as the row
/// query.
/// </summary>
[ConformanceSpec(
    "list-job-alerts.filter-matrix",
    "ListJobAlerts filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobAlerts filters partition the alert rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.",
    Arrange = "Alert rows are seeded per-test in isolation along the filtered dimension.",
    Act = "ListJobAlerts runs once per filter dimension with the opt-in total.",
    Assert = "The returned alert-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ListJobAlertsAsync))]
public abstract class ListJobAlertsFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private Task RaiseAsync(
        IDbSession db,
        long? jobId,
        AlertSeverityCode severity,
        AlertDeliveryStatusCode delivery,
        CancellationToken ct,
        string ns = null!,
        AlertOriginCode origin = AlertOriginCode.Manual,
        AlertKindCode kind = AlertKindCode.Manual
    ) =>
        AlertTestOps.RaiseAsync(
            Services,
            ns ?? TestNamespace,
            jobId,
            origin,
            severity,
            kind,
            "title",
            "message",
            "default",
            delivery,
            null,
            null,
            ct
        );

    private async Task<IReadOnlyList<AlertListItem>> AllAlertsAsync(CancellationToken ct, string ns = null!) =>
        (
            await Services
                .GetRequiredService<IActaOperations>()
                .Alerts.ListAsync(new ListAlertsQuery(JobNamespace: ns ?? TestNamespace, PageSize: 100), ct)
        ).Items;

    [Fact(DisplayName = "JobId filter returns only that job's alerts and excludes all other jobs' alerts")]
    public async Task JobId_filter_returns_exact_alert_id_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Two alerts under j1, one under j2
        const long j1 = 1L,
            j2 = 2L;
        await RaiseAsync(Db, j1, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, j1, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, j2, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);

        // Capture IDs via namespace-only read (independent of jobId filter)
        var all = await AllAlertsAsync(ct);
        var aIds = all.Where(a => a.JobId == j1).Select(a => a.AlertId).ToHashSet();
        var bIds = all.Where(a => a.JobId == j2).Select(a => a.AlertId).ToHashSet();
        Assert.Equal(2, aIds.Count);
        Assert.Equal(1, bIds.Count);

        // Filter by j1: exact set + total, j2 excluded
        var j1Page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, JobId: j1, IncludeTotal: true), ct);
        Assert.Equal(aIds, [.. j1Page.Items.Select(a => a.AlertId)]);
        Assert.Equal(2L, j1Page.TotalCount);
        Assert.Empty(j1Page.Items.Select(a => a.AlertId).Intersect(bIds));

        // Filter by j2: exact set, j1 excluded
        var j2Page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, JobId: j2), ct);
        Assert.Equal(bIds, [.. j2Page.Items.Select(a => a.AlertId)]);
        Assert.Empty(j2Page.Items.Select(a => a.AlertId).Intersect(aIds));
    }

    [Fact(DisplayName = "DeliveryStatus filter partitions alerts by status and the total matches the filtered count")]
    public async Task DeliveryStatus_filter_partitions_by_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Two Pending, one Failed
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Failed, ct);

        // Capture IDs via namespace-only read (independent of delivery-status filter)
        var all = await AllAlertsAsync(ct);
        var pendingIds = all.Where(a => a.DeliveryStatus == AlertDeliveryStatusCode.Pending).Select(a => a.AlertId).ToHashSet();
        var failedIds = all.Where(a => a.DeliveryStatus == AlertDeliveryStatusCode.Failed).Select(a => a.AlertId).ToHashSet();
        Assert.Equal(2, pendingIds.Count);
        Assert.Equal(1, failedIds.Count);

        // Filter by Pending
        var pPage = await queries.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, DeliveryStatus: AlertDeliveryStatusCode.Pending, IncludeTotal: true),
            ct
        );
        Assert.Equal(pendingIds, [.. pPage.Items.Select(a => a.AlertId)]);
        Assert.Equal(2L, pPage.TotalCount);
        Assert.Empty(pPage.Items.Select(a => a.AlertId).Intersect(failedIds));

        // Filter by Failed
        var fPage = await queries.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, DeliveryStatus: AlertDeliveryStatusCode.Failed),
            ct
        );
        Assert.Equal(failedIds, [.. fPage.Items.Select(a => a.AlertId)]);
        Assert.Empty(fPage.Items.Select(a => a.AlertId).Intersect(pendingIds));
    }

    [Fact(DisplayName = "SeverityAtLeast floor returns only alerts at or above the threshold and excludes lower ones")]
    public async Task SeverityAtLeast_floor_excludes_rows_below_threshold()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // One alert per severity level
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Warning, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Error, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Critical, AlertDeliveryStatusCode.Pending, ct);

        // Capture IDs via namespace-only read (independent of severity floor filter)
        var all = await AllAlertsAsync(ct);
        var lowIds = all.Where(a => a.Severity < AlertSeverityCode.Error).Select(a => a.AlertId).ToHashSet(); // Info + Warning
        var highIds = all.Where(a => a.Severity >= AlertSeverityCode.Error).Select(a => a.AlertId).ToHashSet(); // Error + Critical
        Assert.Equal(2, lowIds.Count);
        Assert.Equal(2, highIds.Count);

        // Floor = Error: Error and Critical returned, Info and Warning excluded
        var floorPage = await queries.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, SeverityAtLeast: AlertSeverityCode.Error, IncludeTotal: true),
            ct
        );
        Assert.Equal(highIds, [.. floorPage.Items.Select(a => a.AlertId)]);
        Assert.Equal(2L, floorPage.TotalCount);
        Assert.Empty(floorPage.Items.Select(a => a.AlertId).Intersect(lowIds));
    }

    [Fact(DisplayName = "UnresolvedOnly filter excludes resolved alerts and includes them when filter is null")]
    public async Task UnresolvedOnly_excludes_resolved_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Alert A: Automatic + FirstFailure kind so ResolveJobAlerts will close it
        const long resolveJobId = 1L;
        await RaiseAsync(
            Db,
            resolveJobId,
            AlertSeverityCode.Warning,
            AlertDeliveryStatusCode.Pending,
            ct,
            origin: AlertOriginCode.Automatic,
            kind: AlertKindCode.FirstFailure
        );

        // Alert B: Manual: ResolveJobAlerts ignores it
        await RaiseAsync(Db, null, AlertSeverityCode.Warning, AlertDeliveryStatusCode.Pending, ct);

        // Capture IDs before resolving (via origin discrimination, independent of unresolvedOnly filter)
        var all = await AllAlertsAsync(ct);
        var aId = all.Single(a => a.Origin == AlertOriginCode.Automatic).AlertId;
        var bId = all.Single(a => a.Origin == AlertOriginCode.Manual).AlertId;

        // Resolve alert A
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Services.GetRequiredService<IAlertStore>().ResolveJobAlertsAsync(nsId, resolveJobId, ct);

        // unresolvedOnly=true: only B
        var unresolvedPage = await queries.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, UnresolvedOnly: true, IncludeTotal: true),
            ct
        );
        Assert.Equal([bId], unresolvedPage.Items.Select(a => a.AlertId).ToHashSet());
        Assert.Equal(1L, unresolvedPage.TotalCount);
        Assert.Empty(unresolvedPage.Items.Select(a => a.AlertId).Intersect([aId]));

        // unresolvedOnly=null: both A (resolved) and B (open)
        var allPage = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct);
        Assert.Equal([aId, bId], allPage.Items.Select(a => a.AlertId).ToHashSet());
    }

    [Fact(DisplayName = "JobNamespace filter scopes alerts to exactly one namespace and excludes all other namespaces")]
    public async Task Namespace_filter_isolates_to_one_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Register a second namespace for alert isolation
        var ns2Name = TestKey("ns2");
        var seeder = new ActaTestSeeder(Db);
        await seeder.SeedJobNamespaceAsync(ns2Name, "test", ct);

        // Two alerts in the primary namespace, one in the second
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct);
        await RaiseAsync(Db, null, AlertSeverityCode.Info, AlertDeliveryStatusCode.Pending, ct, ns: ns2Name);

        // Read each namespace independently
        var ns1Page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace, IncludeTotal: true), ct);
        var ns2Page = await queries.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: ns2Name, IncludeTotal: true), ct);

        var ns1Ids = ns1Page.Items.Select(a => a.AlertId).ToHashSet();
        var ns2Ids = ns2Page.Items.Select(a => a.AlertId).ToHashSet();

        // Pin counts to the number we seeded
        Assert.Equal(2L, ns1Page.TotalCount);
        Assert.Equal(1L, ns2Page.TotalCount);
        Assert.Equal(2, ns1Ids.Count);
        Assert.Equal(1, ns2Ids.Count);

        // Cross-exclusion: neither namespace bleeds into the other
        Assert.Empty(ns1Ids.Intersect(ns2Ids));
    }
}
