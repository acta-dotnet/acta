using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The lineage map's active-wait projection. It shares the explainer's precedence (signal, then child
/// latch, then timer) and carries each wait's stored expiration, so a map shows what a parent is
/// parked on and when it gives up. A capped set keeps the rows nearest the focus.
/// </summary>
public class JobLineageMapperTests
{
    private static readonly DateTime Now = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_map_keeps_the_newest_steps_and_nearest_ancestors_and_flags_the_rest()
    {
        // As the read hands them over: steps newest first, ancestors farthest first, one past each limit.
        var steps = Enumerable
            .Range(0, GetJobLineageMapHandler.StepLimit + 1)
            .Select(i => new GetJobLineageMapStepRow($"step-{GetJobLineageMapHandler.StepLimit - i:D3}", StepStatusCode.Succeeded))
            .ToList();
        var ancestors = Enumerable.Range(0, GetJobLineageMapHandler.AncestorLimit + 1).Select(_ => Focus()).ToList();

        var map = GetJobLineageMapHandler.Map(new GetJobLineageMapRows(Focus(), ancestors, steps, [], []), childLimit: 10);

        Assert.True(map.StepsHasMore);
        Assert.Equal(GetJobLineageMapHandler.StepLimit, map.Steps.Count);
        Assert.Equal("step-001", map.Steps[0].Name);
        Assert.Equal($"step-{GetJobLineageMapHandler.StepLimit:D3}", map.Steps[^1].Name);
        Assert.True(map.AncestorsHasMore);
        Assert.Equal(GetJobLineageMapHandler.AncestorLimit, map.Ancestors.Count);
        Assert.Same(ancestors[1], map.Ancestors[0]);
        Assert.Same(ancestors[^1], map.Ancestors[^1]);
        Assert.False(GetJobLineageMapHandler.Map(new GetJobLineageMapRows(Focus(), [], steps[1..], [], []), 10).StepsHasMore);
    }

    [Fact]
    public void A_bounded_child_wait_is_the_active_wait_and_carries_its_deadline()
    {
        var map = Map(Checkpoint(CheckpointKindCode.ChildLatch, "sys.child.42", Now.AddMinutes(30)));

        Assert.NotNull(map.ActiveWait);
        Assert.Equal(CheckpointKindCode.ChildLatch, map.ActiveWait!.Kind);
        Assert.Equal("sys.child.42", map.ActiveWait.Name);
        Assert.Equal(Now.AddMinutes(30), map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void An_unbounded_child_wait_is_the_active_wait_with_no_deadline()
    {
        var map = Map(Checkpoint(CheckpointKindCode.ChildLatch, "sys.child.42", due: null));

        Assert.NotNull(map.ActiveWait);
        Assert.Equal(CheckpointKindCode.ChildLatch, map.ActiveWait!.Kind);
        Assert.Null(map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void A_bounded_signal_wait_carries_its_deadline_and_still_outranks_a_child_latch()
    {
        var map = Map(
            Checkpoint(CheckpointKindCode.ChildLatch, "sys.child.42", Now.AddMinutes(5)),
            Checkpoint(CheckpointKindCode.Signal, "approval", Now.AddMinutes(30))
        );

        Assert.Equal(CheckpointKindCode.Signal, map.ActiveWait!.Kind);
        Assert.Equal(Now.AddMinutes(30), map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void A_set_child_latch_is_not_an_active_wait()
    {
        var map = Map(new GetJobLineageMapCheckpointRow(CheckpointKindCode.ChildLatch, "sys.child.42", CheckpointStatusCode.Set, null));

        Assert.Null(map.ActiveWait);
    }

    private static GetJobLineageMapCheckpointRow Checkpoint(CheckpointKindCode kind, string name, DateTime? due) =>
        new(kind, name, CheckpointStatusCode.Pending, due);

    private static GetJobLineageMapResponse Map(params GetJobLineageMapCheckpointRow[] checkpoints) =>
        GetJobLineageMapHandler.Map(new GetJobLineageMapRows(Focus(), [], [], checkpoints, []), childLimit: 10);

    private static GetJobLineageMapNode Focus() =>
        new(
            JobId: 4821,
            JobRef: new JobRef(Guid.NewGuid()),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Suspended,
            ParentJobId: null,
            ParentJobRef: null,
            LineageRootJobId: null,
            LineageRootJobRef: null,
            CreatedAtUtc: Now,
            ModifiedAtUtc: Now
        );
}
