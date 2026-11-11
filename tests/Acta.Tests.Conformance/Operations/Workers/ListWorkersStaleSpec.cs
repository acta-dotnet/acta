using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// Conformance for a listed worker's staleness: judged on the database clock against the deployment's lease window, the
/// window the overview's stale count uses on the HTTP read.
/// </summary>
[ConformanceSpec(
    "list-workers.stale",
    "ListWorkers marks a live worker silent past the lease window stale",
    Area = "Reads",
    Contract = "ListWorkers marks a live worker stale once its last heartbeat is older than LeaseTtlSeconds on the database clock, "
        + "and never a Dead one."
)]
[Deploys("add-numbers")]
public sealed class ListWorkersStaleSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ListWorkersStaleSpec>>
{
    [Fact(DisplayName = "A live worker silent past the lease window lists as stale, and a fresh or dead one does not")]
    public async Task Stale_is_judged_against_the_lease_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var lease = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var (_, fresh) = await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-fresh", "v1", null, null, 0, 1, ct);
        var (_, silent) = await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-silent", "v1", null, null, 0, 1, ct);
        var (_, dead) = await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-dead", "v1", null, null, 0, 1, ct);

        // A minute past the lease, and well inside the five-minute and longer dead-after windows the suite's global reapers
        // use, so no concurrent sweep turns the silent worker Dead.
        var past = DateTime.UtcNow.AddSeconds(-(lease + 60));
        await Db.From<WorkerEntity>().Where(w => w.Id == silent).UpdateOnlyAsync(() => new WorkerEntity { LastSeenAtUtc = past }, ct);
        await Db.From<WorkerEntity>()
            .Where(w => w.Id == dead)
            .UpdateOnlyAsync(() => new WorkerEntity { Status = WorkerStatusCode.Dead, LastSeenAtUtc = past }, ct);

        var page = await Services.GetRequiredService<IWorkers>().ListAsync(new ListWorkersQuery(TestNamespace, PageSize: 100), ct);
        var stale = page.Items.ToDictionary(worker => worker.WorkerId, worker => worker.Stale);

        Assert.False(stale[fresh]);
        Assert.True(stale[silent]);
        Assert.False(stale[dead]);
    }
}
