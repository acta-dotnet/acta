using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the single-statement park admission on the <c>sys.outbox</c> inbox: insert, reject, the inclusive stale cutoff, and a retried park.</summary>
[ConformanceSpec(
    "park-outbox-signal.admission",
    "Park admits a command into a free or stale inbox slot and rejects a live one",
    Area = "Outbox",
    Contract = "ParkOutboxSignal inserts into a free slot, supersedes a command parked at or before the cutoff, and otherwise rejects with the incumbent's park instant."
)]
public sealed class ParkOutboxSignalSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime NothingIsStale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const byte Json = 1;

    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "outbox-slot-def", ct: TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A park into a free slot inserts a Set signal row at version 0 and reads as Applied with its own park instant")]
    public async Task Park_into_a_free_slot_inserts()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] command = [1, 2, 3];

        var admission = await ParkAsync(jobId, OutboxSignalNames.Requeue, command, NothingIsStale, ct);

        var row = await RowAsync(jobId, OutboxSignalNames.Requeue, ct);
        Assert.Equal((byte)ControlAction.Applied, admission.Action);
        Assert.Equal(row.ModifiedAtUtc, admission.PendingSinceUtc);
        Assert.Equal(CheckpointStatusCode.Set, row.Status);
        Assert.Equal(Json, row.ValueFormatId);
        Assert.Equal(command, row.Value);
        Assert.Equal(0, row.Version);
    }

    [Fact(DisplayName = "A park while a younger command pends is Rejected with the incumbent's park instant and leaves it untouched")]
    public async Task Park_over_a_live_command_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] pending = [1];
        await ParkAsync(jobId, OutboxSignalNames.Discard, pending, NothingIsStale, ct);
        var incumbent = await RowAsync(jobId, OutboxSignalNames.Discard, ct);

        var admission = await ParkAsync(jobId, OutboxSignalNames.Discard, [2], NothingIsStale, ct);

        Assert.Equal((byte)ControlAction.Rejected, admission.Action);
        Assert.Equal(incumbent.ModifiedAtUtc, admission.PendingSinceUtc);
        var after = await RowAsync(jobId, OutboxSignalNames.Discard, ct);
        Assert.Equal(pending, after.Value);
        Assert.Equal(incumbent.Version, after.Version);
        Assert.Equal(incumbent.ModifiedAtUtc, after.ModifiedAtUtc);
    }

    [Fact(DisplayName = "The stale cutoff is inclusive: a command parked at it is superseded and one a millisecond younger is not")]
    public async Task Stale_cutoff_is_inclusive()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);
        var parkedAt = (await RowAsync(jobId, OutboxSignalNames.Requeue, ct)).ModifiedAtUtc;

        var early = await ParkAsync(jobId, OutboxSignalNames.Requeue, [2], parkedAt.AddMilliseconds(-1), ct);
        Assert.Equal((byte)ControlAction.Rejected, early.Action);

        byte[] superseding = [3];
        var atCutoff = await ParkAsync(jobId, OutboxSignalNames.Requeue, superseding, parkedAt, ct);
        Assert.Equal((byte)ControlAction.Applied, atCutoff.Action);

        var row = await RowAsync(jobId, OutboxSignalNames.Requeue, ct);
        Assert.Equal(superseding, row.Value);
        Assert.Equal(1, row.Version);
        Assert.True(row.ModifiedAtUtc >= parkedAt, "a supersede re-stamps the park instant");
    }

    [Fact(DisplayName = "A retried park of the same command reads as landed and writes nothing again")]
    public async Task Retried_park_of_the_same_command_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] command = [7, 7, 7];
        await ParkAsync(jobId, OutboxSignalNames.Requeue, command, NothingIsStale, ct);
        var parked = await RowAsync(jobId, OutboxSignalNames.Requeue, ct);

        // The payload carries the command's minted id, so equal bytes are the same command: a caller that
        // lost the first answer learns its park landed.
        var retried = await ParkAsync(jobId, OutboxSignalNames.Requeue, command, NothingIsStale, ct);

        Assert.Equal((byte)ControlAction.Applied, retried.Action);
        var row = await RowAsync(jobId, OutboxSignalNames.Requeue, ct);
        Assert.Equal(parked.Version, row.Version);
        Assert.Equal(parked.ModifiedAtUtc, row.ModifiedAtUtc);
    }

    [Fact(DisplayName = "Each command name on each job is its own slot")]
    public async Task Names_and_jobs_are_independent_slots()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        var otherJobId = await SeedSlotJobAsync(ct);
        byte[] requeue = [1];
        byte[] discard = [2];
        byte[] otherRequeue = [3];

        Assert.Equal((byte)ControlAction.Applied, (await ParkAsync(jobId, OutboxSignalNames.Requeue, requeue, NothingIsStale, ct)).Action);
        Assert.Equal((byte)ControlAction.Applied, (await ParkAsync(jobId, OutboxSignalNames.Discard, discard, NothingIsStale, ct)).Action);
        Assert.Equal(
            (byte)ControlAction.Applied,
            (await ParkAsync(otherJobId, OutboxSignalNames.Requeue, otherRequeue, NothingIsStale, ct)).Action
        );

        Assert.Equal(requeue, (await RowAsync(jobId, OutboxSignalNames.Requeue, ct)).Value);
        Assert.Equal(discard, (await RowAsync(jobId, OutboxSignalNames.Discard, ct)).Value);
        Assert.Equal(otherRequeue, (await RowAsync(otherJobId, OutboxSignalNames.Requeue, ct)).Value);
    }

    private Task<ParkOutboxSignalResponse> ParkAsync(
        long jobId,
        string name,
        byte[] value,
        DateTime staleBeforeUtc,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<ParkOutboxSignalHandler>()
            .HandleAsync(new ParkOutboxSignalCommand(jobId, name, Json, value, staleBeforeUtc), ct);

    private async Task<CheckpointEntity> RowAsync(long jobId, string name, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.Name == name)
                .ToListAsync(ct)
        );

    private async Task<long> SeedSlotJobAsync(CancellationToken ct) => (await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct)).JobId;
}
