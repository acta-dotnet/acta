using Acta.Relational.Entities;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A parent replays by starting its named child again, which dedupes onto the existing row and reads
/// its result. A child that finished long before its parent, under a parent that waits, sleeps, or is
/// paused past the child's retention deadline, would otherwise be purged, and the replay would run the
/// child a second time. The sweep and the manual purge therefore keep a completed child while its
/// parent is live, and release it once the parent is terminal, so a finished tree still drains.
/// </summary>
[ConformanceSpec(
    "child-jobs.retention-under-live-parent",
    "A completed child outlives its retention deadline while its parent is live",
    Area = "ChildJobs",
    Contract = "Retention and manual purge keep a completed child whose parent is not terminal, and release it, leaves first, once the parent is.",
    Arrange = "A parent starts one named child, the child completes, and the child's retention deadline is moved into the past while the parent is still live.",
    Act = "The retention sweep runs, a manual purge of the child is attempted, the parent replays, and the sweep runs again once the whole tree is terminal and expired.",
    Assert = "The child survives the sweep and the purge, the replay reads its result without running it again, and the terminal tree drains child first, then parent."
)]
[CoversStoreMethod(typeof(IRetentionStore), nameof(IRetentionStore.PurgeBatchAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PurgeJobAsync))]
public abstract class ChildRetentionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A completed child past its deadline survives the sweep and the purge under a live parent, and the replay reads it"
    )]
    public async Task Completed_child_under_live_parent_survives_until_the_tree_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var childId = Assert.Single(await ChildIdsAsync(parent.JobId, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(childId, ct));
        await ExpireAsync(childId, ct);

        // The parent is Ready, released by the child's latch and not yet replayed: live, not terminal.
        await SweepAsync(ct);
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(JobLookup.ById(childId), ct))!.Doubled);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct))!.Doubled);
        Assert.Equal(childId, Assert.Single(await ChildIdsAsync(parent.JobId, ct)));
        Assert.Equal(1, (await Jobs.GetAsync(JobLookup.ById(childId), ct))!.ExecutionNumber);

        // Terminal and expired on both rows: the child is a leaf, so it goes first and the parent follows.
        await ExpireAsync(parent.JobId, ct);
        await RetentionTestOps.PurgeUntilAsync(
            Services,
            NamespaceId,
            365,
            365,
            86400,
            100,
            5,
            async () => await Jobs.GetAsync(parent, ct) is null,
            ct
        );
        Assert.Null(await Jobs.GetAsync(JobLookup.ById(childId), ct));
    }

    [Fact(
        DisplayName = "Manual purge refuses a child under a live parent and a parent with children, and takes the tree leaves first once it is terminal"
    )]
    public async Task Manual_purge_takes_a_terminal_tree_leaves_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var childId = Assert.Single(await ChildIdsAsync(parent.JobId, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(childId, ct));
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(parent, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(parent, ct: ct)).Action);
    }

    private async Task<List<long>> ChildIdsAsync(long parentId, CancellationToken ct) =>
        (await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct)).Select(j => j.Id).ToList();

    private Task ExpireAsync(long jobId, CancellationToken ct) =>
        Db.From<JobRuntime>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(() => new JobRuntime { RetentionUntilUtc = DateTime.UtcNow.AddMinutes(-1) }, ct);

    private Task SweepAsync(CancellationToken ct) => RetentionTestOps.PurgeAsync(Services, NamespaceId, 365, 365, 86400, 100, 5, ct);
}
