using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the projector's auto-resolve on success: which rows it closes, the projection-mark replay guard, and its scope.</summary>
[ConformanceSpec(
    "resolve-job-alerts.on-success",
    "A success resolves its job's open automatic failure alerts once",
    Area = "Alerts",
    Contract = "ResolveAlerts closes one job's open automatic failure alerts marked before the success event, stamps that event as the mark, and returns the count."
)]
public sealed class ResolveAlertsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private int DefId;
    private long JobIdValue;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "resolve-def", ct: ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(
        DisplayName = "Every open automatic failure kind on the job closes, manual alerts and other jobs stay open, and the count says how many"
    )]
    public async Task Open_automatic_failure_alerts_close_and_are_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (otherJobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
        foreach (var kind in new[] { AlertKindCode.FirstFailure, AlertKindCode.ThresholdReached, AlertKindCode.FinalFailure })
        {
            await RaiseAsync(JobIdValue, AlertOriginCode.Automatic, kind, sourceEventId: null, ct);
        }
        await RaiseAsync(JobIdValue, AlertOriginCode.Manual, AlertKindCode.Manual, sourceEventId: null, ct);
        await RaiseAsync(otherJobId, AlertOriginCode.Automatic, AlertKindCode.FinalFailure, sourceEventId: null, ct);
        var before = (await ReadAlertsAsync(TestNamespaceId, ct)).ToDictionary(a => a.Id);

        Assert.Equal(3, await ResolveAsync(TestNamespaceId, JobIdValue, 500, ct));

        var after = await ReadAlertsAsync(TestNamespaceId, ct);
        var closed = after.Where(a => a.ResolvedAtUtc is not null).ToList();
        Assert.Equal(3, closed.Count);
        Assert.All(closed, a => Assert.Equal(JobIdValue, a.JobId));
        Assert.All(closed, a => Assert.Equal(AlertOriginCode.Automatic, a.OriginCode));
        Assert.All(closed, a => Assert.Equal(500L, a.LastProjectedEventId));
        Assert.All(closed, a => Assert.Equal(before[a.Id].Version + 1, a.Version));

        var open = after.Where(a => a.ResolvedAtUtc is null).ToList();
        Assert.Single(open, a => a.JobId == JobIdValue && a.OriginCode == AlertOriginCode.Manual);
        Assert.Single(open, a => a.JobId == otherJobId);
        Assert.All(open, a => Assert.Equal(before[a.Id].Version, a.Version));
    }

    [Fact(DisplayName = "A success no newer than the alert's projection mark closes nothing, and the next newer one does")]
    public async Task Success_at_or_before_the_mark_closes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await RaiseAsync(JobIdValue, AlertOriginCode.Automatic, AlertKindCode.FirstFailure, sourceEventId: 200, ct);
        var raised = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        // A replayed success sitting behind the failure that raised the row, and one at the mark itself.
        Assert.Equal(0, await ResolveAsync(TestNamespaceId, JobIdValue, 150, ct));
        Assert.Equal(0, await ResolveAsync(TestNamespaceId, JobIdValue, 200, ct));
        var held = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Null(held.ResolvedAtUtc);
        Assert.Equal(200L, held.LastProjectedEventId);
        Assert.Equal(raised.Version, held.Version);

        Assert.Equal(1, await ResolveAsync(TestNamespaceId, JobIdValue, 201, ct));
        var resolved = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.NotNull(resolved.ResolvedAtUtc);
        Assert.Equal(201L, resolved.LastProjectedEventId);
    }

    [Fact(DisplayName = "A second success closes nothing and leaves the first resolution exactly as it was")]
    public async Task Second_success_closes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await RaiseAsync(JobIdValue, AlertOriginCode.Automatic, AlertKindCode.FinalFailure, sourceEventId: null, ct);
        Assert.Equal(1, await ResolveAsync(TestNamespaceId, JobIdValue, 300, ct));
        var first = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        Assert.Equal(0, await ResolveAsync(TestNamespaceId, JobIdValue, 400, ct));

        var after = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(first.ResolvedAtUtc, after.ResolvedAtUtc);
        Assert.Equal(300L, after.LastProjectedEventId);
        Assert.Equal(first.Version, after.Version);
    }

    [Fact(DisplayName = "A resolve naming another namespace, or a job with no open alerts, closes nothing")]
    public async Task Resolve_is_scoped_to_the_namespace_and_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherNamespaceId = await Seeder.SeedJobNamespaceAsync(TestKey("other-ns"), ct: ct);
        var (quietJobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
        await RaiseAsync(JobIdValue, AlertOriginCode.Automatic, AlertKindCode.FinalFailure, sourceEventId: null, ct);
        var raised = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));

        Assert.Equal(0, await ResolveAsync(otherNamespaceId, JobIdValue, 500, ct));
        Assert.Equal(0, await ResolveAsync(TestNamespaceId, quietJobId, 500, ct));

        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Null(row.ResolvedAtUtc);
        Assert.Equal(raised.Version, row.Version);
    }

    private Task<int> ResolveAsync(int namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
        Services.GetRequiredService<AlertLifecycle>().ResolveAlertsAsync(namespaceId, jobId, sourceEventId, ct);

    /// <summary>
    /// A keyless raise, so every call is its own row. The source event id stands in for the failure event the
    /// projector would pass: a mark compared by value, not a reference to an events row.
    /// </summary>
    private Task<AlertOccurrence> RaiseAsync(
        long jobId,
        AlertOriginCode origin,
        AlertKindCode kind,
        long? sourceEventId,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<AlertLifecycle>()
            .RaiseAlertAsync(
                RaiseAlertCommand.Create(
                    TestNamespace,
                    jobId,
                    origin,
                    AlertSeverityCode.Error,
                    kind,
                    title: "failing",
                    message: "the job failed",
                    channelName: "ops",
                    AlertDeliveryStatusCode.Pending,
                    deduplicationKey: null,
                    sourceEventId
                ),
                ct
            );
}
