using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s event delete (<c>PurgeExpiredEvents</c>): <c>events</c> rows past the one
/// retention window go, the sys namespace's tenant events included, and a pass reads each index entry once, every
/// batch reading on from where the one before it stopped. The window is driven to deterministic boundaries by passing
/// one wide enough to exclude everything (large positive), a cutoff in the future (negative), or a window rows are aged
/// past, so no real-time wait is needed.
/// </summary>
[ConformanceSpec(
    "purge-expired-events.sweeps",
    "Purge deletes expired events a batch at a time, reading each entry once",
    Area = "Retention",
    Contract = "Purge deletes events past the retention window, the sys namespace's included, and each batch of a pass reads on from where the one before it stopped."
)]
[Deploys("add-numbers")]
public sealed class PurgeExpiredEventsSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredEventsSpec>>
{
    // A window that makes the dated deletes a no-op for the rows produced in these tests: a huge positive
    // retention pushes the cutoff far into the past, so recent rows survive.
    private const int NoPurgeDays = 100_000;

    // A window the boundary facts age their rows past.
    private const int WindowDays = 30;

    [Fact(DisplayName = "Expired events are deleted and recent events are kept")]
    public async Task Expired_events_are_deleted_and_recent_events_survive()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Drive one job (future retention) so claim/start/finish events exist in the namespace.
        await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 1), ct);
        var before = await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct);
        Assert.NotEmpty(before);

        // A wide retention window leaves recent events untouched.
        var keep = await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, 1000, 50, ct);
        Assert.Equal(0, keep.Events);
        Assert.NotEmpty(await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct));

        // A negative window puts the cutoff in the future, so every event is past it. Drained, because a
        // single sweep passes over a row another transaction holds under READPAST.
        var purged = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            -1,
            1000,
            50,
            async () => (await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct)).Count == 0,
            ct
        );
        Assert.Equal(before.Count, purged.Events);
        Assert.Empty(await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct));
    }

    [Fact(DisplayName = "The automatic sweep also deletes the sys namespace's expired tenant events and keeps its recent ones")]
    public async Task Expired_sys_tenant_events_are_deleted_and_recent_ones_survive()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var tenants = Services.GetRequiredService<ITenants>();
        var key = Unique("retention-tenant");
        var tenantId = (await tenants.RegisterAsync(key, null, null, ct)).TenantId;

        // Tenant events belong to the sys namespace, where no worker runs a sweep of its own.
        await tenants.SuspendAsync(key, ct: ct);
        var aged = await Db.From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Tenant && e.EntityId == tenantId && e.EventCode == EventCode.TenantSuspended)
            .SingleOrDefaultAsync(ct);
        Assert.Equal(1, aged!.NamespaceId);
        var agedAt = DateTime.UtcNow.AddDays(-(WindowDays + 1));
        await Db.From<EventEntity>()
            .Where(e => e.WorkerId == aged.WorkerId && e.Id == aged.Id)
            .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = agedAt }, ct);
        await tenants.ResumeAsync(key, ct: ct);

        // A positive window: the recent sys rows other specs write and count stay inside it.
        await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            WindowDays,
            1000,
            50,
            async () => await Db.From<EventEntity>().Where(e => e.Id == aged.Id).SingleOrDefaultAsync(ct) is null,
            ct,
            sweepSystemEvents: true
        );

        Assert.Null(await Db.From<EventEntity>().Where(e => e.Id == aged.Id).SingleOrDefaultAsync(ct));
        Assert.NotNull(
            await Db.From<EventEntity>()
                .Where(e => e.EntityCode == EntityCode.Tenant && e.EntityId == tenantId && e.EventCode == EventCode.TenantResumed)
                .SingleOrDefaultAsync(ct)
        );
    }

    [Fact(DisplayName = "A pass over more events than several batches deletes them all, each batch reading on from the last")]
    public async Task A_pass_reads_each_index_entry_once()
    {
        var ct = TestContext.Current.CancellationToken;
        // A namespace of its own, where no worker writes, so every batch takes exactly the events this fact staged.
        var seeder = new ActaTestSeeder(Db);
        var ns = await seeder.SeedJobNamespaceAsync(Unique("events-pass") + "-" + Guid.NewGuid().ToString("N")[..8], ct: ct);
        // The events are a real job's, this fact's own: a manual purge in another fact deletes a job's events by its id.
        var (job, _) = await seeder.SeedJobAsync(ns, ct: ct);
        var staged = DateTime.UtcNow.AddDays(-(WindowDays + 1));
        for (var i = 0; i < 25; i++)
        {
            // Two events an instant, so a batch stops and reads on inside an instant, by event id.
            await StagedEvents.InsertAsync(
                Db,
                new EventEntity
                {
                    EventCode = EventCode.JobNoteRecorded,
                    NamespaceId = ns,
                    EntityCode = EntityCode.Job,
                    EntityId = job,
                },
                details: null,
                ct,
                staged.AddSeconds(i / 2)
            );
        }
        var entries = (await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct))
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToList();
        var maintenance = Services.GetRequiredService<MaintenanceOperations>();
        var cutoff = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        // The pass holds the position as sys.retention does, starting at the beginning.
        var batch = new PurgeExpiredEventsCommand(ns, cutoff, BatchSize: 10, DateTime.MinValue, 0);
        List<PurgeExpiredEventsResponse> answers = [];
        for (var call = 0; call < 4; call++)
        {
            var answer = await maintenance.PurgeExpiredEventsAsync(batch, ct);
            answers.Add(answer);
            batch = batch with { AfterCreatedUtc = answer.LastCreatedUtc, AfterEventId = answer.LastEventId };
        }

        // Each batch took the next ten entries: its last is the tenth, the twentieth, and the last, and the empty batch
        // after them answers the position it was given.
        Assert.Equal([10, 10, 5, 0], answers.Select(a => a.Purged));
        long[] lasts = [entries[9].Id, entries[19].Id, entries[24].Id, entries[24].Id];
        Assert.Equal(lasts, answers.Select(a => a.LastEventId));
        DateTime[] instants = [entries[9].CreatedAtUtc, entries[19].CreatedAtUtc, entries[24].CreatedAtUtc, entries[24].CreatedAtUtc];
        Assert.Equal(instants, answers.Select(a => a.LastCreatedUtc));
        Assert.Empty(await Db.From<EventEntity>().Where(e => e.NamespaceId == ns).ToListAsync(ct));
    }
}
