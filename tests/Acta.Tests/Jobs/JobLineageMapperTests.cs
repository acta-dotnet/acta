using Acta.Runtime.Modules.Execution.Jobs;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The lineage map's active-wait projection. It shares the explainer's precedence (signal, then child
/// latch, then timer) and carries each wait's stored expiration, so a map shows what a parent is
/// parked on and when it gives up.
/// </summary>
public class JobLineageMapperTests
{
    private static readonly DateTime Now = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_bounded_child_wait_is_the_active_wait_and_carries_its_deadline()
    {
        var map = Map(Checkpoint(JobCheckpointKindCode.ChildLatch, "sys.child.42", Now.AddMinutes(30)));

        Assert.NotNull(map.ActiveWait);
        Assert.Equal(JobCheckpointKindCode.ChildLatch, map.ActiveWait!.Kind);
        Assert.Equal("sys.child.42", map.ActiveWait.Name);
        Assert.Equal(Now.AddMinutes(30), map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void An_unbounded_child_wait_is_the_active_wait_with_no_deadline()
    {
        var map = Map(Checkpoint(JobCheckpointKindCode.ChildLatch, "sys.child.42", due: null));

        Assert.NotNull(map.ActiveWait);
        Assert.Equal(JobCheckpointKindCode.ChildLatch, map.ActiveWait!.Kind);
        Assert.Null(map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void A_bounded_signal_wait_carries_its_deadline_and_still_outranks_a_child_latch()
    {
        var map = Map(
            Checkpoint(JobCheckpointKindCode.ChildLatch, "sys.child.42", Now.AddMinutes(5)),
            Checkpoint(JobCheckpointKindCode.Signal, "approval", Now.AddMinutes(30))
        );

        Assert.Equal(JobCheckpointKindCode.Signal, map.ActiveWait!.Kind);
        Assert.Equal(Now.AddMinutes(30), map.ActiveWait.DueAtUtc);
    }

    [Fact]
    public void A_set_child_latch_is_not_an_active_wait()
    {
        var map = Map(new ExplainCheckpointRow(JobCheckpointKindCode.ChildLatch, "sys.child.42", JobCheckpointStatusCode.Set, null));

        Assert.Null(map.ActiveWait);
    }

    private static ExplainCheckpointRow Checkpoint(JobCheckpointKindCode kind, string name, DateTime? due) =>
        new(kind, name, JobCheckpointStatusCode.Pending, due);

    private static JobLineageMap Map(params ExplainCheckpointRow[] checkpoints) =>
        JobLineageMapper.Map(new JobLineageData(Focus(), [], [], checkpoints, []), childLimit: 10);

    private static LineageJobRow Focus() =>
        new(
            JobId: 4821,
            JobRef: Guid.NewGuid(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Suspended,
            ParentJobId: null,
            ParentJobRef: null,
            LineageRootId: null,
            LineageRootJobRef: null,
            CreatedAtUtc: Now,
            ModifiedAtUtc: Now
        );
}
