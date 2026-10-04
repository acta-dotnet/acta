using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>Conformance for namespace resume: the status flips back with one 15xx event, and the facade rejects sys.</summary>
[ConformanceSpec(
    "resume-namespace.status-flip",
    "Namespace resume flips status back, emits one 15xx event, and rejects sys",
    Area = "Admin",
    Contract = "Resume flips a suspended namespace back to Active and emits namespace.resumed, while sys is rejected at the facade."
)]
public sealed class ResumeNamespaceSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private async Task<JobNamespace?> ReadNsAsync(CancellationToken ct) =>
        await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);

    [Fact(DisplayName = "Resuming a suspended namespace applies and emits namespace.resumed")]
    public async Task Resume_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Operations.Namespaces.SuspendAsync(TestNamespace, null, "op-1", ct);
        var outcome = await Operations.Namespaces.ResumeAsync(TestNamespace, "back", "op-1", ct);
        Assert.Equal(ControlAction.Applied, outcome.Action);
        Assert.Equal(NamespaceStatusCode.Active, (await ReadNsAsync(ct))!.Status);
        Assert.Equal(
            1,
            await Db.From<JobEvent>().Where(e => e.NamespaceId == nsId && e.EventCode == EventCode.NamespaceResumed).CountAsync(ct)
        );
    }

    [Fact(DisplayName = "A rejected sys resume leaves the seeded row untouched")]
    public async Task Sys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<JobNamespace>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Operations.Namespaces.ResumeAsync("sys", null, null, ct));

        var after = await Db.From<JobNamespace>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);
        Assert.Equal(NamespaceStatusCode.Active, after!.Status);
        Assert.Equal(before!.Version, after.Version);
    }
}
