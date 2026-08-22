using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Namespaces;

/// <summary>Conformance for namespace suspend/resume: idempotent status flip, version bump, one 15xx event to the namespace itself, NotFound, and the facade sys guardrail.</summary>
[ConformanceSpec(
    "suspend-resume-namespace.status-flip",
    "Namespace suspend/resume flip status, emit one 15xx event, and reject sys",
    Area = "Admin",
    Contract = "Suspend and resume flip namespace status with a version bump, emit namespace.suspended/resumed to the namespace, and reject sys at the facade.",
    Arrange = "The worker registers the test namespace.",
    Act = "The namespace is suspended, suspended again, resumed, an unknown name is attempted, and sys is attempted through the facade.",
    Assert = "Suspend/resume apply with a version bump and one event each, repeats are AlreadyInState, unknown names NotFound, and sys throws with its row untouched."
)]
[CoversStoreMethod(typeof(INamespaceStore), nameof(INamespaceStore.SuspendNamespaceAsync))]
[CoversStoreMethod(typeof(INamespaceStore), nameof(INamespaceStore.ResumeNamespaceAsync))]
public abstract class SuspendResumeNamespaceSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private static JobControlActor Actor() => new(ActorCode.Operator, "op-1");

    private async Task<JobNamespace?> ReadNsAsync(CancellationToken ct) =>
        await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int nsId, EventCode code, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.NamespaceId == nsId && e.EventCode == code).CountAsync(ct);

    [Fact(DisplayName = "Suspending an active namespace applies, bumps version, and emits one namespace.suspended to itself")]
    public async Task Suspend_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var before = await ReadNsAsync(ct);

        var outcome = await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, Actor(), "hold"), ct);

        Assert.Equal(AdminControlAction.Applied, outcome.Action);
        var after = await ReadNsAsync(ct);
        Assert.Equal(NamespaceStatusCode.Suspended, after!.Status);
        Assert.True(after.Version > before!.Version);
        Assert.Equal(1, await EventCountAsync(nsId, EventCode.NamespaceSuspended, ct));
        var ev = await Db.From<JobEvent>()
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
        await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, Actor(), null), ct);
        var again = await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, Actor(), null), ct);
        Assert.Equal(AdminControlAction.AlreadyInState, again.Action);
        Assert.Equal(1, await EventCountAsync(nsId, EventCode.NamespaceSuspended, ct));
    }

    [Fact(DisplayName = "Resuming a suspended namespace applies and emits namespace.resumed")]
    public async Task Resume_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, Actor(), null), ct);
        var outcome = await Services
            .GetRequiredService<INamespaceStore>()
            .ResumeNamespaceAsync(new NamespaceControlCommand(TestNamespace, Actor(), "back"), ct);
        Assert.Equal(AdminControlAction.Applied, outcome.Action);
        Assert.Equal(NamespaceStatusCode.Active, (await ReadNsAsync(ct))!.Status);
        Assert.Equal(1, await EventCountAsync(nsId, EventCode.NamespaceResumed, ct));
    }

    [Fact(DisplayName = "An unknown namespace name is NotFound")]
    public async Task Unknown_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand("no-such-namespace-xyz", Actor(), null), ct);
        Assert.Equal(AdminControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "Rejected sys suspend/resume leave the seeded row untouched and still listed")]
    public async Task Sys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<JobNamespace>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Operations.Namespaces.SuspendAsync("sys", null, null, ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Operations.Namespaces.ResumeAsync("sys", null, null, ct));

        var after = await Db.From<JobNamespace>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);
        Assert.Equal(NamespaceStatusCode.Active, after!.Status);
        Assert.Equal(before!.Version, after.Version);

        var page = await Operations.Namespaces.ListNamesAsync(new ListNamespacesQuery(NameContains: "sys"), ct);
        Assert.Contains("sys", page.Items);
    }
}
