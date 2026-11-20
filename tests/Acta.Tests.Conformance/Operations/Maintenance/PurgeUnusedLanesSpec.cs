using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s lane delete (<c>PurgeUnusedLanes</c>) batch by batch: a batch takes at most its
/// size of one namespace's unreferenced lanes, in id order. Each fact seeds namespaces of its own, so every lane a
/// batch can see is the fact's; <c>LaneRetentionSpec</c> holds the delete against live and racing jobs.
/// </summary>
[ConformanceSpec(
    "purge-unused-lanes.batches",
    "Purge deletes a namespace's unreferenced lanes a batch at a time",
    Area = "Lanes",
    Contract = "A batch deletes at most its size of one namespace's unreferenced lanes, and never another namespace's."
)]
[Deploys("add-numbers")]
public sealed class PurgeUnusedLanesSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeUnusedLanesSpec>>
{
    [Fact(DisplayName = "Batches take a namespace's unreferenced lanes up to their size until none is left, and leave another namespace's")]
    public async Task Batches_take_one_namespaces_lanes_up_to_their_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeder = new ActaTestSeeder(Db);
        var ns = await seeder.SeedJobNamespaceAsync(Unique("lanes") + "-" + Guid.NewGuid().ToString("N")[..8], ct: ct);
        var other = await seeder.SeedJobNamespaceAsync(Unique("lanes-other") + "-" + Guid.NewGuid().ToString("N")[..8], ct: ct);
        foreach (var lane in new[] { Unique("lane-a"), Unique("lane-b"), Unique("lane-c") })
        {
            await Db.From<LaneEntity>().InsertAsync<long>(new LaneEntity { NamespaceId = ns, LaneName = lane }, ct);
        }
        await Db.From<LaneEntity>().InsertAsync<long>(new LaneEntity { NamespaceId = other, LaneName = Unique("lane-a") }, ct);
        var maintenance = Services.GetRequiredService<MaintenanceOperations>();

        List<int> purged = [];
        for (var batch = 0; batch < 3; batch++)
        {
            purged.Add((await maintenance.PurgeUnusedLanesAsync(new PurgeUnusedLanesCommand(ns, BatchSize: 2), ct)).Purged);
        }

        Assert.Equal([2, 1, 0], purged);
        Assert.Empty(await Db.From<LaneEntity>().Where(l => l.NamespaceId == ns).ToListAsync(ct));
        Assert.Single(await Db.From<LaneEntity>().Where(l => l.NamespaceId == other).ToListAsync(ct));
    }
}
