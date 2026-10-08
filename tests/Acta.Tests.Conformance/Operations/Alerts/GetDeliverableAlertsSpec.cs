using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the deliver-phase read: its namespace scope, order, batch cap, row content, and the unset-instant rule per arm.</summary>
[ConformanceSpec(
    "get-deliverable-alerts.scope-and-order",
    "The delivery read takes one namespace's due alerts in id order without a lease",
    Area = "Alerts",
    Contract = "GetDeliverableAlerts reads one namespace's due alerts in id order up to the batch size, with each job's runbook and refs, and writes nothing."
)]
public sealed class GetDeliverableAlertsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private int DefId;
    private long JobIdValue;
    private Guid JobRefValue;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "deliverable-def", ct: ct);
        (JobIdValue, JobRefValue) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(DisplayName = "Due alerts come back in id order and the batch size caps one read")]
    public async Task Due_alerts_read_in_id_order_up_to_the_batch_size()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 3; i++)
        {
            await RaiseAsync(TestNamespace, jobId: null, ct);
        }

        var ids = (await ReadAlertsAsync(TestNamespaceId, ct)).Select(a => a.Id).Order().ToList();

        Assert.Equal(ids.Take(2), (await DueAsync(TestNamespaceId, 2, ct)).Select(a => a.AlertId));
        Assert.Equal(ids, (await DueAsync(TestNamespaceId, 50, ct)).Select(a => a.AlertId));
    }

    [Fact(DisplayName = "The read takes no lease: a repeat hands out the same rows at the same versions and the rows are unchanged")]
    public async Task The_read_takes_no_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        await RaiseAsync(TestNamespace, jobId: null, ct);
        await RaiseAsync(TestNamespace, JobIdValue, ct);
        var before = (await ReadAlertsAsync(TestNamespaceId, ct)).OrderBy(a => a.Id).ToList();

        var first = await DueAsync(TestNamespaceId, 50, ct);
        var second = await DueAsync(TestNamespaceId, 50, ct);

        Assert.Equal(first.Select(a => (a.AlertId, a.Version)), second.Select(a => (a.AlertId, a.Version)));
        Assert.Equal(before.Select(a => (a.Id, a.Version)), first.Select(a => (a.AlertId, a.Version)));
        var after = (await ReadAlertsAsync(TestNamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(
            before.Select(a => (a.Version, a.DeliveryStatusCode, a.ModifiedAtUtc)),
            after.Select(a => (a.Version, a.DeliveryStatusCode, a.ModifiedAtUtc))
        );
    }

    [Fact(DisplayName = "Only the named namespace's alerts are read")]
    public async Task Only_the_named_namespace_is_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherNamespace = TestKey("other-ns");
        var otherNamespaceId = await Seeder.SeedJobNamespaceAsync(otherNamespace, ct: ct);
        await RaiseAsync(TestNamespace, jobId: null, ct);
        await RaiseAsync(otherNamespace, jobId: null, ct);

        var ours = Assert.Single(await DueAsync(TestNamespaceId, 50, ct));
        var theirs = Assert.Single(await DueAsync(otherNamespaceId, 50, ct));

        Assert.Equal(Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct)).Id, ours.AlertId);
        Assert.Equal(Assert.Single(await ReadAlertsAsync(otherNamespaceId, ct)).Id, theirs.AlertId);
    }

    [Fact(DisplayName = "A job's alert carries its definition's effective runbook URL and both refs, and a job-less alert carries no job")]
    public async Task Rows_carry_the_runbook_and_the_refs()
    {
        var ct = TestContext.Current.CancellationToken;
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == DefId)
            .UpdateOnlyAsync(
                () =>
                    new DefinitionEntity
                    {
                        RunbookUrl = "https://runbooks.example/base",
                        RunbookUrlOverride = "https://runbooks.example/override",
                    },
                ct
            );
        await RaiseAsync(TestNamespace, JobIdValue, ct);
        await RaiseAsync(TestNamespace, jobId: null, ct);
        var rows = (await ReadAlertsAsync(TestNamespaceId, ct)).ToDictionary(a => a.Id);

        var due = await DueAsync(TestNamespaceId, 50, ct);

        var bound = Assert.Single(due, a => a.JobId == JobIdValue);
        Assert.Equal("https://runbooks.example/override", bound.RunbookUrl);
        Assert.Equal(JobRefValue, bound.JobRef);
        AssertMatchesRow(rows[bound.AlertId], bound);

        var jobless = Assert.Single(due, a => a.JobId is null);
        Assert.Null(jobless.RunbookUrl);
        Assert.Null(jobless.JobRef);
        AssertMatchesRow(rows[jobless.AlertId], jobless);
    }

    [Fact(DisplayName = "An unset retry instant is due at once for an unsettled row and never for a settled one")]
    public async Task Unset_instant_is_due_only_on_the_retry_arm()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<AlertLifecycle>();
        var retrying = await StageAsync(AlertDeliveryStatusCode.RetryAfter, ct);
        await StageAsync(AlertDeliveryStatusCode.Delivered, ct);
        await StageAsync(AlertDeliveryStatusCode.Failed, ct);

        // A settled row is reminded only at the instant its settlement scheduled, so with none set it
        // waits for the next settle rather than going out on every pass.
        Assert.Equal([retrying], (await store.GetDeliverableAlertsAsync(TestNamespaceId, 50, ct)).Select(a => a.AlertId));
    }

    private static void AssertMatchesRow(AlertEntity row, DeliverableAlert due)
    {
        Assert.Equal(row.AlertRef, due.AlertRef);
        Assert.Equal(row.JobRef, due.JobRef);
        Assert.Equal(row.Version, due.Version);
        Assert.Equal(row.OriginCode, due.Origin);
        Assert.Equal(row.SeverityCode, due.Severity);
        Assert.Equal(row.Kind, due.Kind);
        Assert.Equal(row.Title, due.Title);
        Assert.Equal(row.Message, due.Message);
        Assert.Equal(row.ChannelName, due.ChannelName);
        Assert.Equal(row.OccurrenceCount, due.OccurrenceCount);
        Assert.Equal(row.RetryCount, due.RetryCount);
        Assert.Equal(row.CreatedAtUtc, due.CreatedAtUtc);
    }

    private Task<IReadOnlyList<DeliverableAlert>> DueAsync(int namespaceId, int batchSize, CancellationToken ct) =>
        Services.GetRequiredService<AlertLifecycle>().GetDeliverableAlertsAsync(namespaceId, batchSize, ct);

    /// <summary>One job-less alert settled into <paramref name="status"/> with no retry instant; returns its id.</summary>
    private async Task<long> StageAsync(AlertDeliveryStatusCode status, CancellationToken ct)
    {
        var key = TestKey("unset-" + status.ToString().ToLowerInvariant());
        await RaiseAsync(TestNamespace, jobId: null, ct, key);
        var row = Assert.Single(
            await Db.From<AlertEntity>().Where(a => a.NamespaceId == TestNamespaceId && a.DedupeKey == key).ToListAsync(ct)
        );
        Assert.True(
            (
                await Services
                    .GetRequiredService<AlertLifecycle>()
                    .UpdateAlertDeliveryAsync(row.Id, row.Version, status, retryCount: 1, retryAfterUtc: null, ct)
            ).Updated
        );
        return row.Id;
    }

    private Task RaiseAsync(string jobNamespace, long? jobId, CancellationToken ct, string? deduplicationKey = null) =>
        AlertTestOps.RaiseAsync(
            Services,
            jobNamespace,
            jobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "deliverable",
            message: "due for delivery",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            ct
        );
}
