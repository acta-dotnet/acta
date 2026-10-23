using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// Two relay passes can read the same parked command when an attempt of <c>sys.outbox</c> outlives its lease.
/// A command parks at version 0 whenever its name is free, so a command parked after the faster pass consumed
/// the first one carries the version the slower pass read; the slower pass's consume must still miss it.
/// </summary>
[ConformanceSpec(
    "outbox-signal.stale-pass",
    "A stale relay pass never consumes a command parked after the one it read",
    Area = "Outbox",
    Contract = "A pass that read a command another pass already consumed leaves a command parked since then in place, and the next pass applies that command once."
)]
public sealed class OutboxRelaySignalOverlapSpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "A command parked while a stale pass is mid-apply survives that pass and the next pass applies it once")]
    public async Task Command_parked_during_a_stale_pass_survives_and_applies_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var signals = Services.GetRequiredService<OutboxOperations>();
        var first = EchoRow(Unique("stale-first"), status: 90, failureCount: 5);
        var second = EchoRow(Unique("stale-second"), status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, first);
        await Fixture.SeedOutboxRowAsync(SourceTable, second);
        var slotJobId = await SeedSlotJobAsync(ct);
        Assert.Equal(1, await ParkAsync(slotJobId, OutboxSignalNames.Requeue, "first command", [first.OutboxId], ct));

        // The stale pass reads the first command, then holds before its source requeue.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleStore = new HookedOutboxStore(SourceConnection)
        {
            BeforeRequeue = async () =>
            {
                reached.TrySetResult();
                await release.Task;
            },
        };
        var stale = Relay(staleStore.Source, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        // The faster pass applies and consumes the first command; the operator then parks the second.
        Assert.Equal((1, 0), await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct));
        Assert.Equal(1, await ParkAsync(slotJobId, OutboxSignalNames.Requeue, "second command", [second.OutboxId], ct));

        release.SetResult();
        Assert.Equal((0, 0), await stale);

        Assert.NotNull(await signals.GetOutboxSignalAsync(slotJobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal((1, 0), await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct));
        Assert.Null(await signals.GetOutboxSignalAsync(slotJobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(SourceTable, second.OutboxId)).StatusCode);

        var events = await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == slotJobId).ToListAsync(ct);
        var applied = Assert.Single(events, e => e.EventMessage?.Contains("second command", StringComparison.Ordinal) == true);
        Assert.Contains(second.OutboxId.ToString(), applied.EventMessage);
    }
}
