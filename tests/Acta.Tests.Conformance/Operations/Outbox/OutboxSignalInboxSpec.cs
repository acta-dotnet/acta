using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the sys.outbox operator-command inbox: park admission (insert when free, reject
/// while a younger command pends, supersede once the pending one has outlived the worker-dead
/// window) and the version-CAS consume that closes the loop. The version bump on supersede is what
/// makes a consume racing an overwrite miss instead of deleting the newer command.
/// </summary>
[ConformanceSpec(
    "outbox-signal.inbox",
    "Park admission and version-CAS consume bound the operator inbox",
    Area = "Outbox",
    Contract = "Park inserts when free, rejects with the pending instant while a younger command pends, supersedes a stale one bumping the version consume must match to delete."
)]
public sealed class OutboxSignalInboxSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "Free and stale slots admit, a pending slot rejects with its age, and consume is version-CAS")]
    public async Task Park_admission_and_consume_follow_the_inbox_contract()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<OutboxOperations>();
        var park = Services.GetRequiredService<ParkOutboxSignalHandler>();
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);

        var nothingIsStale = DateTime.UtcNow.AddHours(-1);
        byte[] first = [1, 1, 1];

        var admitted = await park.HandleAsync(
            new ParkOutboxSignalCommand(jobId, OutboxSignalNames.Requeue, ValueFormatId: 1, first, nothingIsStale),
            ct
        );
        Assert.Equal(1, admitted.Action);

        // A second command while one is pending is rejected with the incumbent's park instant.
        var rejected = await park.HandleAsync(
            new ParkOutboxSignalCommand(jobId, OutboxSignalNames.Requeue, ValueFormatId: 1, [2, 2, 2], nothingIsStale),
            ct
        );
        Assert.Equal(3, rejected.Action);
        Assert.NotNull(rejected.PendingSinceUtc);

        var pending = (await store.GetOutboxSignalAsync(jobId, OutboxSignalNames.Requeue, ct))?.Signal;
        Assert.NotNull(pending);
        Assert.Equal(first, pending!.Value);
        Assert.Equal(0, pending.Version);

        // Once the pending command has outlived the worker-dead window, a new one supersedes it and
        // the version bump invalidates the old command's consume.
        var everythingIsStale = DateTime.UtcNow.AddHours(1);
        byte[] superseding = [3, 3, 3];
        var superseded = await park.HandleAsync(
            new ParkOutboxSignalCommand(jobId, OutboxSignalNames.Requeue, ValueFormatId: 1, superseding, everythingIsStale),
            ct
        );
        Assert.Equal(1, superseded.Action);

        Assert.False(
            (await store.ConsumeOutboxSignalAsync(jobId, OutboxSignalNames.Requeue, version: 0, ct)).Consumed,
            "a superseded version must miss"
        );
        var current = (await store.GetOutboxSignalAsync(jobId, OutboxSignalNames.Requeue, ct))?.Signal;
        Assert.NotNull(current);
        Assert.Equal(superseding, current!.Value);
        Assert.Equal(1, current.Version);

        Assert.True((await store.ConsumeOutboxSignalAsync(jobId, OutboxSignalNames.Requeue, version: 1, ct)).Consumed);
        Assert.Null(await store.GetOutboxSignalAsync(jobId, OutboxSignalNames.Requeue, ct));

        // The two fixed names are independent slots: the discard slot was never touched.
        Assert.Null(await store.GetOutboxSignalAsync(jobId, OutboxSignalNames.Discard, ct));
    }
}
