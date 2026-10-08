using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>Conformance for namespace suspend: idempotent status flip, version bump, one 15xx event to the namespace itself, NotFound, and the facade sys guardrail.</summary>
[ConformanceSpec(
    "suspend-namespace.status-flip",
    "Namespace suspend flips status, emits one 15xx event, and rejects sys",
    Area = "Admin",
    Contract = "Suspend flips a namespace to Suspended once with a version bump and namespace.suspended, reports NotFound for unknown names, and rejects sys."
)]
public sealed class SuspendNamespaceSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private async Task<NamespaceEntity?> ReadNsAsync(CancellationToken ct) =>
        await Db.From<NamespaceEntity>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int nsId, EventCode code, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.NamespaceId == nsId && e.EventCode == code).CountAsync(ct);

    [Fact(DisplayName = "Suspending an active namespace applies, bumps version, and emits one namespace.suspended to itself")]
    public async Task Suspend_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var before = await ReadNsAsync(ct);

        var outcome = await Operations.Namespaces.SuspendAsync(TestNamespace, "hold", "op-1", ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var after = await ReadNsAsync(ct);
        Assert.Equal(NamespaceStatusCode.Suspended, after!.Status);
        Assert.True(after.Version > before!.Version);
        Assert.Equal(1, await EventCountAsync(nsId, EventCode.NamespaceSuspended, ct));
        var ev = await Db.From<EventEntity>()
            .Where(e => e.NamespaceId == nsId && e.EventCode == EventCode.NamespaceSuspended)
            .SingleOrDefaultAsync(ct);
        Assert.Equal(nsId, ev!.NamespaceId);
        Assert.Equal("op-1", ev.ActorKey);
        Assert.Equal("hold", ev.ReasonMessage);
    }

    [Fact(DisplayName = "Re-suspending is AlreadyInState with no second event")]
    public async Task Suspend_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Operations.Namespaces.SuspendAsync(TestNamespace, null, "op-1", ct);
        var again = await Operations.Namespaces.SuspendAsync(TestNamespace, null, "op-1", ct);
        Assert.Equal(ControlAction.AlreadyInState, again.Action);
        Assert.Equal(1, await EventCountAsync(nsId, EventCode.NamespaceSuspended, ct));
    }

    [Fact(DisplayName = "An unknown namespace name is NotFound")]
    public async Task Unknown_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Operations.Namespaces.SuspendAsync("no-such-namespace-xyz", null, "op-1", ct);
        Assert.Equal(ControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "A rejected sys suspend leaves the seeded row untouched and still listed")]
    public async Task Sys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<NamespaceEntity>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Operations.Namespaces.SuspendAsync("sys", null, null, ct));

        var after = await Db.From<NamespaceEntity>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);
        Assert.Equal(NamespaceStatusCode.Active, after!.Status);
        Assert.Equal(before!.Version, after.Version);

        var page = await Operations.Namespaces.ListNamesAsync(new ListNamespaceNamesQuery(NameContains: "sys"), ct);
        Assert.Contains("sys", page.Items);
    }
}
