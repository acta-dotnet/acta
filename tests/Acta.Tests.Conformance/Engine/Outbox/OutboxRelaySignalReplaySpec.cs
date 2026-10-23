using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// An operator command's plan, stored in its own row before the source changes, bounds every later pass over it. A
/// pass that replays the command after a crash between its source change and its evidence records one event naming
/// the planned rows and changes no row quarantined since, and a pass that read the command before another planned it
/// stops before the source.
/// </summary>
[ConformanceSpec(
    "outbox-signal.replay",
    "A replayed operator command records one event and changes only its planned rows",
    Area = "Outbox",
    Contract = "A command replayed after a crash before its evidence records one event naming its planned rows, and a pass that loses the plan never touches the source."
)]
public sealed class OutboxRelaySignalReplaySpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "A requeue replayed after a crash before its evidence records one event for its planned rows only")]
    public async Task Replay_after_a_crash_records_one_event_for_the_planned_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var signals = Services.GetRequiredService<OutboxOperations>();
        var first = EchoRow(Unique("replay-first"), status: 90, failureCount: 5);
        var second = EchoRow(Unique("replay-second"), status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, first);
        await Fixture.SeedOutboxRowAsync(SourceTable, second);
        var slotJobId = await SeedSlotJobAsync(ct);
        Assert.Equal(1, await ParkAsync(slotJobId, OutboxSignalNames.Requeue, "every quarantined row", null, ct));

        // The crashing pass plans and requeues, then dies before the statement that records and consumes.
        var crashing = ActivatorUtilities.CreateInstance<OutboxOperations>(
            Services,
            new ConsumeOutboxSignalHandler(
                new HookedSession(Services.GetRequiredService<IDbSession>(), _ => throw new InvalidOperationException("crashed")),
                Services.GetRequiredService<ISqlDialect>()
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, crashing, ct)
        );
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(SourceTable, first.OutboxId)).StatusCode);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(SourceTable, second.OutboxId)).StatusCode);

        var later = EchoRow(Unique("replay-later"), status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, later);

        Assert.Equal((2, 0), await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct));

        Assert.Null(await signals.GetOutboxSignalAsync(slotJobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(SourceTable, later.OutboxId)).StatusCode);
        var evidence = Assert.Single(await SlotEventsAsync(slotJobId, ct));
        Assert.Contains("2 row(s)", evidence.EventMessage, StringComparison.Ordinal);
        Assert.Contains(first.OutboxId.ToString(), evidence.EventMessage, StringComparison.Ordinal);
        Assert.Contains(second.OutboxId.ToString(), evidence.EventMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A pass that read the command before another pass planned it never touches the source")]
    public async Task Pass_that_loses_the_plan_never_touches_the_source()
    {
        var ct = TestContext.Current.CancellationToken;
        var signals = Services.GetRequiredService<OutboxOperations>();
        var row = EchoRow(Unique("lost-first"), status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, row);
        var slotJobId = await SeedSlotJobAsync(ct);
        Assert.Equal(1, await ParkAsync(slotJobId, OutboxSignalNames.Requeue, "every quarantined row", null, ct));

        // The stale pass reads the command, then holds before it reads the rows to plan.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStore = new HookedOutboxStore(SourceConnection)
        {
            BeforeMatch = async () =>
            {
                reached.TrySetResult();
                await release.Task;
            },
            BeforeRequeue = () => throw new InvalidOperationException("the stale pass touched the source"),
        };
        var stale = Relay(staleStore.Source, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal((1, 0), await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct));
        var later = EchoRow(Unique("lost-later"), status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, later);

        release.SetResult();
        Assert.Equal((0, 0), await stale);

        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(SourceTable, later.OutboxId)).StatusCode);
        Assert.Single(await SlotEventsAsync(slotJobId, ct));
    }

    private async Task<IReadOnlyList<EventEntity>> SlotEventsAsync(long slotJobId, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == slotJobId).ToListAsync(ct);
}
