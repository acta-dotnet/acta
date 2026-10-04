using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for <c>GetStaleChildLatches</c>, the maintenance backstop's feed. A waiting parent holds a
/// Pending <c>sys.child.{id}</c> latch that its child sets on landing terminal; a crash between the two
/// leaves the latch Pending forever, and this read is how the sweep finds it.
/// </summary>
[ConformanceSpec(
    "get-stale-child-latches.backstop",
    "Stale child latches are found once their child is terminal or gone",
    Area = "Execution",
    Contract = "A parent's Pending child latch is reported once its child is terminal or gone, and never while the child is live or once the latch is set."
)]
public sealed class GetStaleChildLatchesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private Acta.Runtime.Execution Execution => Services.GetRequiredService<Acta.Runtime.Execution>();

    [Fact(
        DisplayName = "A Pending latch is not reported while its child is live, and is reported with the child's status once it lands terminal unraised"
    )]
    public async Task Latch_is_reported_once_its_child_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.DoesNotContain(await Execution.GetStaleChildLatchesAsync(NamespaceId, ct), l => l.ParentJobId == parent);

        // The child lands Failed and the raise that should follow is lost, as a crash between the two would leave it.
        var affected = await Db.From<JobRuntime>()
            .Where(r => r.Id == child)
            .UpdateOnlyAsync(() => new JobRuntime { Status = JobStatusCode.Failed }, ct);
        Assert.Equal(1, affected);

        var stale = Assert.Single(await Execution.GetStaleChildLatchesAsync(NamespaceId, ct), l => l.ParentJobId == parent);
        Assert.Equal(new StaleChildLatch(parent, child, JobStatusCode.Failed), stale);
        Assert.DoesNotContain(await Execution.GetStaleChildLatchesAsync(int.MaxValue, ct), l => l.ParentJobId == parent);
    }

    [Fact(DisplayName = "A Pending latch whose child row is gone is reported with no child status")]
    public async Task Latch_whose_child_is_gone_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.Equal(1, await Db.ExecuteRawAsync("DELETE FROM {schema}.jobs WHERE id = @p_id", ct, ("@p_id", child)));

        var stale = Assert.Single(await Execution.GetStaleChildLatchesAsync(NamespaceId, ct), l => l.ParentJobId == parent);
        Assert.Equal(new StaleChildLatch(parent, child, null), stale);
    }

    [Fact(DisplayName = "A latch the child set when it landed is not reported")]
    public async Task Set_latch_is_not_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child, ct));

        Assert.Equal(JobCheckpointStatusCode.Set, Assert.Single(await ReadSignalsAsync(parent, ct)).Status);
        Assert.DoesNotContain(await Execution.GetStaleChildLatchesAsync(NamespaceId, ct), l => l.ParentJobId == parent);
    }

    /// <summary>Runs a parent once so it starts its child and suspends on that child's Pending latch.</summary>
    private async Task<(long Parent, long Child)> ParentWaitingOnChildAsync(CancellationToken ct)
    {
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<Job>().Where(j => j.ParentId == parent.JobId).ToListAsync(ct));
        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal($"sys.child.{child.Id}", latch.Name);
        Assert.Equal(JobCheckpointStatusCode.Pending, latch.Status);
        return (parent.JobId, child.Id);
    }
}
