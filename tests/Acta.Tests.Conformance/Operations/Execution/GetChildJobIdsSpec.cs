using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>GetChildJobIds</c>, the per-level feed of the cancel cascade and the child-wait parentage check.</summary>
[ConformanceSpec(
    "get-child-job-ids.direct",
    "A job's child list holds its direct children, finished ones included",
    Area = "Execution",
    Contract = "The child list of a job holds every job enqueued under it, terminal or not, and nothing from further down the lineage."
)]
public sealed class GetChildJobIdsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private Acta.Runtime.Execution Execution => Services.GetRequiredService<Acta.Runtime.Execution>();

    [Fact(DisplayName = "Every direct child is listed, a cancelled one included")]
    public async Task Every_direct_child_is_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(parentJobId: null, "root", ct);
        var children = new List<EnqueueJobResponse>();
        for (var i = 0; i < 3; i++)
        {
            children.Add(await EnqueueAsync(root.JobId, $"c{i}", ct));
        }
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(children[0], ct: ct)).Action);

        var listed = await Execution.GetChildJobIdsAsync(root.JobId, ct);

        Assert.Equal(children.Select(c => c.JobId).Order(), listed.Order());
    }

    [Fact(DisplayName = "A grandchild is listed under its own parent only, and a job with no children lists none")]
    public async Task Only_direct_children_are_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(parentJobId: null, "root", ct);
        var child = await EnqueueAsync(root.JobId, "child", ct);
        var grandchild = await EnqueueAsync(child.JobId, "grandchild", ct);

        Assert.Equal(child.JobId, Assert.Single(await Execution.GetChildJobIdsAsync(root.JobId, ct)));
        Assert.Equal(grandchild.JobId, Assert.Single(await Execution.GetChildJobIdsAsync(child.JobId, ct)));
        Assert.Empty(await Execution.GetChildJobIdsAsync(grandchild.JobId, ct));
    }

    private async Task<EnqueueJobResponse> EnqueueAsync(long? parentJobId, string key, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "job-child-echo",
                JobPayload.Json(new ChildEcho(1)),
                DeduplicationKey: key,
                ParentJobId: parentJobId
            ),
            ct
        );
}
