using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A parent replays by starting its named child again, which dedupes onto the existing row and reads
/// its result. A child that finished long before its parent, under a parent that waits, sleeps, or is
/// paused past the child's retention deadline, or under a finished parent that a restart replays, would
/// otherwise be purged, and the replay would run the child a second time. The sweep therefore keeps a
/// finished child until its parent has finished and the parent's own retention is due, and trees still
/// drain from the leaves up. It reads the direct parent alone, so a leaf goes once its parent is due even
/// while a grandparent that is not due keeps that parent. The manual purge keeps a completed child while
/// its parent is live.
/// </summary>
[ConformanceSpec(
    "child-jobs.retention-under-live-parent",
    "A finished child is kept until its parent's own retention is due",
    Area = "ChildJobs",
    Contract = "Retention keeps a finished child until its parent has finished and the parent's own retention is due, manual purge while the parent is live."
)]
// purge-now keeps its jobs no time at all, so a sweep walks the namespace from the cutoff and reaches a job that a
// fact expires by hand.
[Deploys("job-child-echo", "job-parent-one", "purge-now")]
public sealed class ChildRetentionSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ChildRetentionSpec>>
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A completed child past its deadline survives the sweep and the purge under a live parent, and the replay reads it"
    )]
    public async Task Completed_child_under_live_parent_survives_until_the_tree_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var childId = Assert.Single(await ChildIdsAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(childId, ct));
        await ExpireAsync(childId, ct);

        // The parent is Ready, released by the child's latch and not yet replayed: live, not terminal.
        await SweepAsync(ct);
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(JobLookup.ById(childId), ct)).Doubled);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct)).Doubled);
        Assert.Equal(childId, Assert.Single(await ChildIdsAsync(parent.JobId, ct)));
        Assert.Equal(1, (await Jobs.GetAsync(JobLookup.ById(childId), ct))!.ExecutionNumber);

        // Terminal and expired on both rows: the child is a leaf, so it goes first and the parent follows.
        await ExpireAsync(parent.JobId, ct);
        await RetentionTestOps.PurgeUntilAsync(Services, NamespaceId, 365, 100, 5, async () => await Jobs.GetAsync(parent, ct) is null, ct);
        Assert.Null(await Jobs.GetAsync(JobLookup.ById(childId), ct));
    }

    [Fact(
        DisplayName = "Manual purge refuses a child under a live parent and a parent with children, and takes the tree leaves first once it is terminal"
    )]
    public async Task Manual_purge_takes_a_terminal_tree_leaves_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var childId = Assert.Single(await ChildIdsAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(childId, ct));
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(ControlAction.Rejected, (await Jobs.PurgeAsync(parent, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(JobLookup.ById(childId), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(parent, ct: ct)).Action);
    }

    [Fact(
        DisplayName = "A finished child past its retention is kept while its finished parent's own retention is not yet due, and a restarted parent reads it"
    )]
    public async Task A_finished_child_is_kept_until_its_finished_parent_is_due()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, childId) = await FinishedTreeAsync(ct);
        await ExpireAsync(childId, ct);
        var now = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        Assert.True((await ReadJobAsync(parent.JobId, ct)).RetentionUntilUtc > now, "the parent is due already");

        await SweepPastAJobWithNoParentAsync(ct);
        Assert.NotNull(await Jobs.GetAsync(JobLookup.ById(childId), ct));

        // The kept row is what a replay dedupes onto: the restarted parent reads the child's first result and runs it no more.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(parent, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct)).Doubled);
        Assert.Equal(childId, Assert.Single(await ChildIdsAsync(parent.JobId, ct)));
        Assert.Equal(1, (await Jobs.GetAsync(JobLookup.ById(childId), ct))!.ExecutionNumber);
    }

    [Fact(
        DisplayName = "Once its parent is due too, one sweep call takes the child and a later one the parent, and nothing of either remains"
    )]
    public async Task Once_the_parent_is_due_the_child_goes_first_and_nothing_remains()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, childId) = await FinishedTreeAsync(ct);
        foreach (var jobId in (long[])[parent.JobId, childId])
        {
            await Operations.Tags.UpsertAsync(TagTarget.ForJob(JobLookup.ById(jobId)), new TagInput("tree", "kept"), ct: ct);
            await ExpireAsync(jobId, ct);
        }
        Assert.NotEmpty(await Db.From<CheckpointEntity>().Where(c => c.JobId == parent.JobId).ToListAsync(ct));

        // The call that takes the child has already passed its parent, which still had a child when it was read.
        var goneAfter = await PassesUntilGoneAsync([childId, parent.JobId], ct);
        Assert.True(goneAfter[0] < goneAfter[1], $"the child went on pass {goneAfter[0]} and its parent on pass {goneAfter[1]}");

        foreach (var jobId in (long[])[parent.JobId, childId])
        {
            Assert.Empty(await Db.From<JobStateEntity>().Where(r => r.Id == jobId).ToListAsync(ct));
            Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == jobId).ToListAsync(ct));
            Assert.Empty(await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct));
            Assert.Empty(await Db.From<TagEntity>().Where(t => t.EntityCode == EntityCode.Job && t.EntityId == jobId).ToListAsync(ct));
        }
    }

    [Fact(DisplayName = "A finished child past its retention is kept while its finished parent has no retention date")]
    public async Task A_finished_child_is_kept_under_a_finished_parent_kept_forever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, childId) = await FinishedTreeAsync(ct);
        await ExpireAsync(childId, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET retention_until_utc = NULL WHERE job_id = @p_id",
            ct,
            ("@p_id", parent.JobId)
        );

        await SweepPastAJobWithNoParentAsync(ct);
        Assert.NotNull(await Jobs.GetAsync(JobLookup.ById(childId), ct));
        Assert.NotNull(await Jobs.GetAsync(parent, ct));
    }

    [Fact(DisplayName = "A job with no parent goes on its own retention date")]
    public async Task A_job_with_no_parent_goes_on_its_own_date()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await EnqueueAndRunAsync("job-child-echo", new ChildEcho(1), ct);
        await ExpireAsync(root.JobId, ct);

        await RetentionTestOps.PurgeUntilAsync(Services, NamespaceId, 365, 100, 5, async () => await Jobs.GetAsync(root, ct) is null, ct);
        Assert.Null(await Jobs.GetAsync(root, ct));
    }

    [Fact(DisplayName = "A three-level tree drains leaves first, one level a sweep call")]
    public async Task A_three_level_tree_drains_leaves_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var (root, middle, leaf) = await FinishedThreeLevelTreeAsync(ct);

        var goneAfter = await PassesUntilGoneAsync([leaf.JobId, middle.JobId, root.JobId], ct);
        Assert.True(
            goneAfter[0] < goneAfter[1] && goneAfter[1] < goneAfter[2],
            $"the leaf went on pass {goneAfter[0]}, the middle on {goneAfter[1]} and the root on {goneAfter[2]}"
        );
    }

    [Fact(
        DisplayName = "In a three-level tree whose root is not yet due, the sweep takes the due leaf and keeps the due middle job and the root"
    )]
    public async Task A_due_leaf_goes_while_a_root_that_is_not_due_keeps_its_middle_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (root, middle, leaf) = await FinishedThreeLevelTreeAsync(ct);
        await Db.From<JobStateEntity>()
            .Where(r => r.Id == root.JobId)
            .UpdateOnlyAsync(() => new JobStateEntity { RetentionUntilUtc = DateTime.UtcNow.AddDays(1) }, ct);

        // The sweep reads a child's direct parent alone: the middle job is due, so its leaf goes, and the root keeps the middle job.
        await RetentionTestOps.PurgeUntilAsync(Services, NamespaceId, 365, 100, 5, async () => await Jobs.GetAsync(leaf, ct) is null, ct);
        Assert.Null(await Jobs.GetAsync(leaf, ct));
        await SweepPastAJobWithNoParentAsync(ct);
        Assert.NotNull(await Jobs.GetAsync(middle, ct));
        Assert.NotNull(await Jobs.GetAsync(root, ct));
    }

    /// <summary>
    /// A root, its child, and that child's child, all finished; each level is started under a parent that is still Ready,
    /// and purge-now keeps each of them no time once it finishes.
    /// </summary>
    private async Task<(EnqueueJobResponse Root, EnqueueJobResponse Middle, EnqueueJobResponse Leaf)> FinishedThreeLevelTreeAsync(
        CancellationToken ct
    )
    {
        var root = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "purge-now", JobPayload.Json(new PurgeProbe("root"))), ct);
        var middle = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "purge-now", JobPayload.Json(new PurgeProbe("middle")), ParentJobId: root.JobId),
            ct
        );
        var leaf = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "purge-now", JobPayload.Json(new PurgeProbe("leaf")), ParentJobId: middle.JobId),
            ct
        );
        foreach (var job in (EnqueueJobResponse[])[leaf, middle, root])
        {
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
        }

        return (root, middle, leaf);
    }

    /// <summary>A parent that called its child, read its result, and finished, both rows stamped with their definitions' retention.</summary>
    private async Task<(EnqueueJobResponse Parent, long ChildId)> FinishedTreeAsync(CancellationToken ct)
    {
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var childId = Assert.Single(await ChildIdsAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(childId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        return (parent, childId);
    }

    /// <summary>
    /// Sweeps until a job with no parent, expired after the fact's own jobs, is gone: the sweep that took it read the
    /// namespace's other finished jobs in the same pass, so a job still there was kept.
    /// </summary>
    private async Task SweepPastAJobWithNoParentAsync(CancellationToken ct)
    {
        var control = await EnqueueAndRunAsync("purge-now", new PurgeProbe("control"), ct);
        await RetentionTestOps.PurgeUntilAsync(
            Services,
            NamespaceId,
            365,
            100,
            5,
            async () => await Jobs.GetAsync(control, ct) is null,
            ct
        );
        Assert.Null(await Jobs.GetAsync(control, ct));
    }

    /// <summary>Sweeps one call a pass until every job is gone, and answers the pass each one went on.</summary>
    private async Task<int[]> PassesUntilGoneAsync(IReadOnlyList<long> jobIds, CancellationToken ct)
    {
        var goneAfter = new int[jobIds.Count];
        var pass = 0;
        await SpecWaits.RepeatUntilAsync(
            async () =>
            {
                pass++;
                await SweepAsync(ct);
                for (var i = 0; i < jobIds.Count; i++)
                {
                    if (goneAfter[i] == 0 && await Jobs.GetAsync(JobLookup.ById(jobIds[i]), ct) is null)
                    {
                        goneAfter[i] = pass;
                    }
                }

                return goneAfter.All(p => p > 0);
            },
            static drained => drained,
            ct
        );
        Assert.All(goneAfter, p => Assert.True(p > 0, "a job of the tree was never purged"));
        return goneAfter;
    }

    private async Task<List<long>> ChildIdsAsync(long parentId, CancellationToken ct) =>
        (await Db.From<JobEntity>().Where(j => j.ParentJobId == parentId).ToListAsync(ct)).Select(j => j.Id).ToList();

    private Task ExpireAsync(long jobId, CancellationToken ct) =>
        Db.From<JobStateEntity>()
            .Where(r => r.Id == jobId)
            .UpdateOnlyAsync(() => new JobStateEntity { RetentionUntilUtc = DateTime.UtcNow.AddMinutes(-1) }, ct);

    private Task SweepAsync(CancellationToken ct) => RetentionTestOps.PurgeAsync(Services, NamespaceId, 365, 100, 5, ct);
}
