using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the version compare-and-swap delete that closes an applied operator command on a <c>sys.outbox</c> slot.</summary>
[ConformanceSpec(
    "consume-outbox-signal.version-cas",
    "Consume deletes an applied command only at the version the tick read",
    Area = "Outbox",
    Contract = "ConsumeOutboxSignal deletes one job's signal row under a name only at the expected version and answers whether it did, so a repeat changes nothing."
)]
public sealed class ConsumeOutboxSignalSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime NothingIsStale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EverythingIsStale = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "outbox-slot-def", ct: TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A consume at the version read deletes the row and answers true, and a repeat answers false")]
    public async Task Consume_at_the_read_version_deletes_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);

        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, version: 0, ct));
        Assert.Equal(0, await CountAsync(jobId, OutboxSignalNames.Requeue, ct));

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, version: 0, ct));
    }

    [Fact(DisplayName = "A consume at a superseded version answers false and the newer command survives")]
    public async Task Consume_at_a_superseded_version_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] newer = [2];
        await ParkAsync(jobId, OutboxSignalNames.Discard, [1], NothingIsStale, ct);
        await ParkAsync(jobId, OutboxSignalNames.Discard, newer, EverythingIsStale, ct);

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Discard, version: 0, ct));

        var survivor = Assert.Single(await RowsAsync(jobId, OutboxSignalNames.Discard, ct));
        Assert.Equal(newer, survivor.Value);
        Assert.Equal(1, survivor.Version);
    }

    [Fact(DisplayName = "A consume of an empty slot answers false")]
    public async Task Consume_of_an_empty_slot_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, version: 0, ct));
    }

    [Fact(DisplayName = "A consume deletes only the named command of that job")]
    public async Task Consume_is_scoped_to_one_job_and_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        var otherJobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);
        await ParkAsync(jobId, OutboxSignalNames.Discard, [2], NothingIsStale, ct);
        await ParkAsync(otherJobId, OutboxSignalNames.Requeue, [3], NothingIsStale, ct);

        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, version: 0, ct));

        Assert.Equal(0, await CountAsync(jobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal(1, await CountAsync(jobId, OutboxSignalNames.Discard, ct));
        Assert.Equal(1, await CountAsync(otherJobId, OutboxSignalNames.Requeue, ct));
    }

    private Task<bool> ConsumeAsync(long jobId, string name, int version, CancellationToken ct) =>
        Services.GetRequiredService<ConsumeOutboxSignalHandler>().HandleAsync(new ConsumeOutboxSignalCommand(jobId, name, version), ct);

    private Task<OutboxSignalAdmissionRow> ParkAsync(
        long jobId,
        string name,
        byte[] value,
        DateTime staleBeforeUtc,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<ParkOutboxSignalHandler>()
            .HandleAsync(new ParkOutboxSignalCommand(jobId, name, ValueFormatId: 1, value, staleBeforeUtc), ct);

    private async Task<IReadOnlyList<CheckpointEntity>> RowsAsync(long jobId, string name, CancellationToken ct) =>
        await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.Name == name)
            .ToListAsync(ct);

    private async Task<int> CountAsync(long jobId, string name, CancellationToken ct) => (await RowsAsync(jobId, name, ct)).Count;

    private async Task<long> SeedSlotJobAsync(CancellationToken ct) => (await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct)).JobId;
}
