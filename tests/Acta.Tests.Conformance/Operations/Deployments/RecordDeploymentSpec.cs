using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Deployments;

/// <summary>
/// Operation-level conformance for <c>RecordDeployment</c>, the last statement of a deploy's transaction: one row per
/// namespace and version, kept from the first time that version deploys; the namespace's current version, owner, and
/// description written only when one of them differs; and a <c>namespace.deployed</c> event for every run.
/// </summary>
[ConformanceSpec(
    "deployment.record",
    "RecordDeployment makes a version current and leaves an event",
    Area = "Catalog",
    Contract = "RecordDeployment inserts the version's row once, writes the namespace only when it differs, and writes a namespace.deployed event every time."
)]
public sealed class RecordDeploymentSpec : ActaStorageTestBase<ConformanceFixture>
{
    private Task RecordAsync(string version, string? ownerTeam, string? description, CancellationToken ct) =>
        RecordAsync(version, ownerTeam, description, previous: null, forced: false, added: [], changed: [], ct);

    private async Task RecordAsync(
        string version,
        string? ownerTeam,
        string? description,
        string? previous,
        bool forced,
        string[] added,
        string[] changed,
        CancellationToken ct
    )
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await InCallerTransactionAsync(
            connection,
            async transaction =>
            {
                await Services
                    .GetRequiredService<DeploymentsOperations>()
                    .RecordDeploymentAsync(
                        new(transaction, TestNamespaceId, version, ownerTeam, description, previous, forced, added, changed),
                        ct
                    );
                await transaction.CommitAsync(ct);
            },
            ct
        );
    }

    private async Task<NamespaceEntity> ReadNamespaceAsync(CancellationToken ct) =>
        (await Db.From<NamespaceEntity>().Where(n => n.Id == TestNamespaceId).SingleOrDefaultAsync(ct))!;

    private async Task<IReadOnlyList<DeploymentEntity>> ReadDeploymentsAsync(CancellationToken ct) =>
        [
            .. (await Db.From<DeploymentEntity>().Where(d => d.NamespaceId == TestNamespaceId).ToListAsync(ct)).OrderBy(d =>
                d.DeploymentVersion
            ),
        ];

    private async Task<IReadOnlyList<EventEntity>> ReadDeployedEventsAsync(CancellationToken ct) =>
        [
            .. (
                await Db.From<EventEntity>()
                    .Where(e => e.NamespaceId == TestNamespaceId && e.EventCode == EventCode.NamespaceDeployed)
                    .ToListAsync(ct)
            ).OrderBy(e => e.Id),
        ];

    [Fact(DisplayName = "Recording a version adds its row and makes it the namespace's current version")]
    public async Task Recording_a_version_adds_its_row_and_makes_it_current()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await ReadNamespaceAsync(ct);

        await RecordAsync("1.0.0+b81d04e", "test", null, ct);

        var after = await ReadNamespaceAsync(ct);
        Assert.Equal("1.0.0+b81d04e", after.DeploymentVersion);
        Assert.Equal(before.Version + 1, after.Version);
        var row = Assert.Single(await ReadDeploymentsAsync(ct));
        Assert.Equal("1.0.0+b81d04e", row.DeploymentVersion);
    }

    [Fact(DisplayName = "Recording the current version again writes neither the row nor the namespace")]
    public async Task Recording_the_current_version_again_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync("1.0.0", "test", null, ct);
        var namespaceBefore = await ReadNamespaceAsync(ct);
        var rowBefore = Assert.Single(await ReadDeploymentsAsync(ct));

        await RecordAsync("1.0.0", "test", null, ct);

        var namespaceAfter = await ReadNamespaceAsync(ct);
        Assert.Equal(namespaceBefore.Version, namespaceAfter.Version);
        Assert.Equal(namespaceBefore.ModifiedAtUtc, namespaceAfter.ModifiedAtUtc);
        Assert.Equal(rowBefore.CreatedAtUtc, Assert.Single(await ReadDeploymentsAsync(ct)).CreatedAtUtc);
    }

    [Fact(DisplayName = "Recording an earlier version makes it current again and keeps the row from its first deploy")]
    public async Task Recording_an_earlier_version_makes_it_current_and_keeps_its_first_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync("1.0.0", "test", null, ct);
        var first = Assert.Single(await ReadDeploymentsAsync(ct));
        await RecordAsync("1.1.0", "test", null, ct);

        await RecordAsync("1.0.0", "test", null, ct);

        Assert.Equal("1.0.0", (await ReadNamespaceAsync(ct)).DeploymentVersion);
        var rows = await ReadDeploymentsAsync(ct);
        Assert.Equal(["1.0.0", "1.1.0"], rows.Select(r => r.DeploymentVersion));
        Assert.Equal(first.CreatedAtUtc, rows[0].CreatedAtUtc);
    }

    [Fact(DisplayName = "Versions that differ only in case are two versions, each with its own row")]
    public async Task Versions_that_differ_only_in_case_are_two_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync("1.4.0-RC1", "test", null, ct);
        var first = await ReadNamespaceAsync(ct);

        await RecordAsync("1.4.0-rc1", "test", null, ct);

        var second = await ReadNamespaceAsync(ct);
        Assert.Equal("1.4.0-rc1", second.DeploymentVersion);
        Assert.Equal(first.Version + 1, second.Version);
        Assert.Equal(2, (await ReadDeploymentsAsync(ct)).Count);
    }

    [Fact(DisplayName = "Recording stores a changed owner or description, and clears one the deploy no longer declares")]
    public async Task Recording_stores_a_changed_owner_or_description_and_clears_an_undeclared_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await RecordAsync("1.0.0", "billing", "Invoices and dunning", ct);
        var declared = await ReadNamespaceAsync(ct);
        Assert.Equal(("billing", "Invoices and dunning"), (declared.OwnerTeam, declared.Description));

        await RecordAsync("1.0.0", "payments", null, ct);

        var changed = await ReadNamespaceAsync(ct);
        Assert.Equal("payments", changed.OwnerTeam);
        Assert.Null(changed.Description);
        Assert.Equal(declared.Version + 1, changed.Version);
        Assert.Single(await ReadDeploymentsAsync(ct));
    }

    [Fact(DisplayName = "Every recording leaves a namespace.deployed event naming the version, the one it replaced, and what changed")]
    public async Task Every_recording_leaves_an_event_naming_the_version_and_what_changed()
    {
        var ct = TestContext.Current.CancellationToken;

        await RecordAsync("1.0.0", "test", null, previous: null, forced: false, added: ["send-invoice", "sys.alerts"], changed: [], ct);
        await RecordAsync("1.1.0", "test", null, previous: "1.0.0", forced: false, added: [], changed: ["send-invoice"], ct);
        await RecordAsync("1.1.0", "test", null, previous: "1.1.0", forced: false, added: [], changed: [], ct);
        await RecordAsync("1.0.0", "test", null, previous: "1.1.0", forced: true, added: [], changed: ["send-invoice"], ct);

        var events = await ReadDeployedEventsAsync(ct);
        Assert.Equal(
            ["1.0.0 deployed", "1.1.0 deployed over 1.0.0", "1.1.0 deployed again", "1.0.0 deployed over 1.1.0"],
            events.Select(e => e.EventMessage)
        );
        Assert.All(events, e => Assert.Equal((EntityCode.Namespace, (long)TestNamespaceId), (e.EntityCode, e.EntityId)));

        Assert.Equal("1.0.0", EventDetail(events[0], "deploymentVersion"));
        Assert.Null(EventDetail(events[0], "previousDeploymentVersion"));
        Assert.Equal("send-invoice, sys.alerts", EventDetail(events[0], "added"));
        Assert.Null(EventDetail(events[0], "changed"));
        Assert.Null(EventDetail(events[0], "forced"));

        Assert.Equal("1.0.0", EventDetail(events[1], "previousDeploymentVersion"));
        Assert.Equal("send-invoice", EventDetail(events[1], "changed"));

        // A run that changed nothing is still evidence that it ran.
        Assert.Null(EventDetail(events[2], "added"));
        Assert.Null(EventDetail(events[2], "changed"));

        Assert.Equal("true", EventDetail(events[3], "forced"));
    }
}
