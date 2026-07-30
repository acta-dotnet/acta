using Acta.Modules.Execution.Jobs;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>GetJobLineageMap</c>: the five-result-set lineage read that returns the focus job,
/// its ancestors (root first), its steps and checkpoints, and its direct children capped at the fetch
/// limit, and <c>null</c> for an unknown id. Exercises the recursive ancestor walk, the child cap, and
/// the absent-row sentinel across the shared (pg/sqlite) and SQL Server SQL variants.
/// </summary>
[ConformanceSpec(
    "get-job-lineage-map.point-read",
    "GetJobLineageMap returns the focus job with ancestors and children or null",
    Area = "Reads",
    Contract = "GetJobLineageMap returns the focus job, its ancestors root-first, its steps and checkpoints, and its capped direct children, or null when no row matches.",
    Arrange = "A parent/child job tree is enqueued so a focus job has ancestors and direct children.",
    Act = "GetJobLineageMap is called on a focus job, on a leaf to read ancestor order, with a small fetch limit, and with an id that matches no row.",
    Assert = "The focus job returns its ancestors root-first and its capped direct children, and the unmatched id returns null."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobLineageMapAsync))]
public abstract class GetJobLineageMapSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private async Task<long> EnqueueAsync(long? parentId, CancellationToken ct)
    {
        var outcome = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), ParentId: parentId),
            ct
        );
        return outcome.JobId;
    }

    [Fact(DisplayName = "A known job returns its focus row, its root parent as an ancestor, and its two direct children")]
    public async Task Returns_focus_ancestors_and_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        var mid = await EnqueueAsync(root, ct);
        var childA = await EnqueueAsync(mid, ct);
        var childB = await EnqueueAsync(mid, ct);

        var data = await Services.GetRequiredService<IJobStore>().GetJobLineageMapAsync(mid, 100, ct);

        Assert.NotNull(data);
        Assert.Equal(mid, data!.Focus.JobId);
        Assert.Equal(root, data.Focus.ParentJobId);
        Assert.Equal(root, data.Focus.LineageRootId);
        Assert.Single(data.Ancestors);
        Assert.Equal(root, data.Ancestors[0].JobId);
        Assert.Null(data.Ancestors[0].ParentJobId);
        Assert.Equal([childA, childB], data.Children.Select(c => c.JobId).OrderBy(id => id).ToArray());
        Assert.Empty(data.Steps);
        Assert.Empty(data.Checkpoints);
    }

    [Fact(DisplayName = "Ancestors are ordered from the lineage root down to the immediate parent")]
    public async Task Ancestors_are_ordered_root_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        var mid = await EnqueueAsync(root, ct);
        var leaf = await EnqueueAsync(mid, ct);

        var data = await Services.GetRequiredService<IJobStore>().GetJobLineageMapAsync(leaf, 100, ct);

        Assert.NotNull(data);
        Assert.Equal([root, mid], data!.Ancestors.Select(a => a.JobId).ToArray());
        Assert.Empty(data.Children);
    }

    [Fact(DisplayName = "The direct-children set is capped at the fetch limit")]
    public async Task Children_are_capped_at_the_fetch_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAsync(null, ct);
        await EnqueueAsync(root, ct);
        await EnqueueAsync(root, ct);
        await EnqueueAsync(root, ct);

        var data = await Services.GetRequiredService<IJobStore>().GetJobLineageMapAsync(root, 2, ct);

        Assert.NotNull(data);
        Assert.Equal(2, data!.Children.Count);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = await Services.GetRequiredService<IJobStore>().GetJobLineageMapAsync(long.MaxValue, 100, ct);

        Assert.Null(data);
    }
}
