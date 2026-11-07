using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for <c>ListStaleJobChildLatches</c>, the maintenance backstop's feed. A waiting parent holds a
/// Pending <c>sys.child.{id}</c> latch that its child sets on landing terminal; a crash between the two
/// leaves the latch Pending forever, and this read is how the sweep finds it. A terminal parent waits on nothing, and a
/// job that is not the parent's child never sets the latch, so neither is the backstop's to raise.
/// </summary>
[ConformanceSpec(
    "list-stale-job-child-latches.backstop",
    "Stale child latches are found once their child is terminal or gone",
    Area = "Execution",
    Contract = "A live parent's Pending latch on its own child is reported once that child is terminal or gone, and never otherwise."
)]
public sealed class ListStaleJobChildLatchesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(
        DisplayName = "A Pending latch is not reported while its child is live, and is reported with the child's status once it lands terminal unraised"
    )]
    public async Task Latch_is_reported_once_its_child_is_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.DoesNotContain((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);

        await LandUnraisedAsync(child, ct);

        var stale = Assert.Single((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);
        Assert.Equal(new ListStaleJobChildLatchesItem(parent, child, JobStatusCode.Failed), stale);
        Assert.DoesNotContain((await Execution.ListStaleJobChildLatchesAsync(int.MaxValue, ct)).Latches, l => l.ParentJobId == parent);
    }

    [Fact(DisplayName = "A cancelled parent's Pending latch is not reported, and a recovery pass leaves it as the cancel did")]
    public async Task Latch_of_a_cancelled_parent_is_not_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);
        await LandUnraisedAsync(child, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(parent), ct: ct)).Action);
        var cancelled = Assert.Single(await ReadSignalsAsync(parent, ct));

        Assert.DoesNotContain((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);

        await Services.GetRequiredService<RecoveryPass>().RunAsync(NamespaceId, TestNamespace, ct);
        var after = Assert.Single(await ReadSignalsAsync(parent, ct));
        Assert.Equal((cancelled.Status, cancelled.Version), (after.Status, after.Version));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(parent, ct)).Status);
    }

    [Fact(
        DisplayName = "A Pending latch naming a job that is not the parent's child is not reported, and a recovery pass leaves the parent waiting"
    )]
    public async Task Latch_on_a_job_that_is_not_the_parents_child_is_not_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, _) = await ParentWaitingOnChildAsync(ct);
        var stranger = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(stranger, ct));

        // The parent's latch now names a finished job it never started, as a wait on a stale or foreign id would.
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET checkpoint_name = @p_name WHERE job_id = @p_job AND checkpoint_kind_code = 50",
                ct,
                ("@p_name", $"sys.child.{stranger.JobId}"),
                ("@p_job", parent)
            )
        );

        Assert.DoesNotContain((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);

        await Services.GetRequiredService<RecoveryPass>().RunAsync(NamespaceId, TestNamespace, ct);
        Assert.Equal(CheckpointStatusCode.Pending, Assert.Single(await ReadSignalsAsync(parent, ct)).Status);
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parent, ct)).Status);
    }

    [Fact(DisplayName = "A Pending latch whose child row is gone is reported with no child status")]
    public async Task Latch_whose_child_is_gone_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.Equal(1, await Db.ExecuteRawAsync("DELETE FROM {schema}.jobs WHERE job_id = @p_id", ct, ("@p_id", child)));

        var stale = Assert.Single((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);
        Assert.Equal(new ListStaleJobChildLatchesItem(parent, child, null), stale);
    }

    [Fact(DisplayName = "A latch the child set when it landed is not reported")]
    public async Task Set_latch_is_not_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await ParentWaitingOnChildAsync(ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));

        Assert.Equal(CheckpointStatusCode.Set, Assert.Single(await ReadSignalsAsync(parent, ct)).Status);
        Assert.DoesNotContain((await StaleAsync(ct)).Latches, l => l.ParentJobId == parent);
    }

    private Task<ListStaleJobChildLatchesResponse> StaleAsync(CancellationToken ct) =>
        Execution.ListStaleJobChildLatchesAsync(NamespaceId, ct);

    /// <summary>The child lands Failed and the raise that should follow is lost, as a crash between the two would leave it.</summary>
    private async Task LandUnraisedAsync(long child, CancellationToken ct) =>
        Assert.Equal(
            1,
            await Db.From<JobStateEntity>()
                .Where(r => r.Id == child)
                .UpdateOnlyAsync(() => new JobStateEntity { Status = JobStatusCode.Failed }, ct)
        );

    /// <summary>Runs a parent once so it starts its child and suspends on that child's Pending latch.</summary>
    private async Task<(long Parent, long Child)> ParentWaitingOnChildAsync(CancellationToken ct)
    {
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));
        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal($"sys.child.{child.Id}", latch.CheckpointName);
        Assert.Equal(CheckpointStatusCode.Pending, latch.Status);
        return (parent.JobId, child.Id);
    }
}
