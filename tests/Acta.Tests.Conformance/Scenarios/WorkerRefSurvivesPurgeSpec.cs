using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for worker identity across retention: <c>events</c> outlives the <c>workers</c> row, so
/// the timeline has to stay readable once the holder is gone. The joined <c>worker_ref</c> is a live
/// lookup and goes null with the row; the denormalized <c>actor</c> is the durable copy and still
/// renders the canonical <c>wrk_</c> string. The internal worker id keeps filtering the surviving rows.
/// </summary>
[ConformanceSpec(
    "worker-ref.survives-purge",
    "Events outlive a purged worker with a canonical actor",
    Area = "Retention",
    Contract = "Purging the workers row leaves its events with a null joined worker ref, a canonical wrk_ actor, and the historical worker id still selecting them."
)]
public sealed class WorkerRefSurvivesPurgeSpec : ActaStorageTestBase<ConformanceFixture>
{
    // Windows wide enough that the jobs, events, and alerts sections are no-ops: only the worker
    // section, driven by a future cutoff, may delete anything in this spec.
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;

    [Fact(
        DisplayName = "After purge the worker row is gone while its events keep a canonical wrk_ actor, a null workerRef, and the historical worker id"
    )]
    public async Task Events_outlive_the_worker_row_with_a_canonical_actor_and_no_worker_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var ledger = Services.GetRequiredService<IActaOperations>().Ledger;
        var workers = Services.GetRequiredService<WorkersOperations>();

        // The ref is minted here so the expected actor is known without reading the row that this
        // spec is about to delete.
        var workerRef = WorkerRef.New();
        var (namespaceId, workerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "purge-host",
            "v1",
            null,
            null,
            4242,
            4,
            ct,
            workerRef.Value
        );
        await workers.StopWorkerAsync(namespaceId, workerId, ct);

        // Before purge: both lifecycle events name the worker twice over - the joined ref and the
        // denormalized actor - which is what makes the after-purge difference meaningful.
        var before = await ledger.ListEventsAsync(new ListEventsQuery(WorkerId: workerId, JobNamespace: TestNamespace), ct);
        Assert.Equal(2, before.Items.Count);
        Assert.Contains(before.Items, e => e.EventCode == EventCode.WorkerStarted);
        Assert.Contains(before.Items, e => e.EventCode == EventCode.WorkerStopped);
        Assert.All(before.Items, e => Assert.Equal<WorkerRef?>(workerRef, e.WorkerRef));
        Assert.All(before.Items, e => Assert.Equal(workerRef.ToString(), e.Actor));

        // Retention reaps both terminal worker statuses, so the cleanly stopped row is purged as it
        // stands: the deletion runs through the production purge routine, which is the path under test.
        // Drain rather than assert one call reaps it: the sweep skips rows another transaction holds.
        var purged = 0;
        for (var pass = 1; pass <= 20 && purged == 0; pass++)
        {
            var result = await RetentionTestOps.PurgeAsync(
                Services,
                namespaceId,
                NoEventPurgeDays,
                NoAlertPurgeDays,
                workerRetentionSeconds: -1,
                1000,
                50,
                ct
            );
            purged += result.Workers;
        }
        Assert.Equal(1, purged);
        Assert.Null(await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct));

        var after = await ledger.ListEventsAsync(new ListEventsQuery(WorkerId: workerId, JobNamespace: TestNamespace), ct);

        // The historical id still selects the rows: events.worker_id carries no foreign key, so purging
        // the holder does not cascade the timeline away.
        Assert.Equal(before.Items.Count, after.Items.Count);
        Assert.Equal(before.Items.Select(e => e.JobEventId).OrderBy(id => id), after.Items.Select(e => e.JobEventId).OrderBy(id => id));

        // The joined ref is a live lookup, so it reads null; the denormalized actor is the durable
        // copy and still renders the canonical wrk_ form rather than the stored uuid text.
        Assert.All(after.Items, e => Assert.Null(e.WorkerRef));
        Assert.All(after.Items, e => Assert.Equal(workerRef.ToString(), e.Actor));
        Assert.All(after.Items, e => Assert.Equal(ActorCode.Worker, e.ActorCode));
        Assert.DoesNotContain(
            after.Items,
            e => string.Equals(e.Actor, workerRef.Value.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        );
    }
}
