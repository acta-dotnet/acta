using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the applying tick's read of one pending operator command on a <c>sys.outbox</c> slot.</summary>
[ConformanceSpec(
    "get-outbox-signal.pending-read",
    "The applying tick reads the pending command under one name, or nothing",
    Area = "Outbox",
    Contract = "GetOutboxSignal returns the Set signal row under one job and name with its format, bytes, and version, and null when that slot holds no pending command."
)]
public sealed class GetOutboxSignalSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime NothingIsStale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EverythingIsStale = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "outbox-slot-def", ct: TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "An empty slot reads null under either command name")]
    public async Task Empty_slot_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);

        Assert.Null(await GetAsync(jobId, OutboxSignalNames.Requeue, ct));
        Assert.Null(await GetAsync(jobId, OutboxSignalNames.Discard, ct));
    }

    [Fact(DisplayName = "A pending command reads back with its format, its bytes, and the version a consume must match")]
    public async Task Pending_command_reads_back_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] first = [1, 1];
        byte[] superseding = [2, 2, 2];
        await ParkAsync(jobId, OutboxSignalNames.Requeue, valueFormatId: 1, first, NothingIsStale, ct);

        var parked = await GetAsync(jobId, OutboxSignalNames.Requeue, ct);
        Assert.NotNull(parked);
        Assert.Equal(1, parked.ValueFormatId);
        Assert.Equal(first, parked.Value);
        Assert.Equal(0, parked.Version);

        await ParkAsync(jobId, OutboxSignalNames.Requeue, valueFormatId: 3, superseding, EverythingIsStale, ct);

        var current = await GetAsync(jobId, OutboxSignalNames.Requeue, ct);
        Assert.NotNull(current);
        Assert.Equal(3, current.ValueFormatId);
        Assert.Equal(superseding, current.Value);
        Assert.Equal(1, current.Version);
    }

    [Fact(DisplayName = "Only a Set signal row of that job and name answers: an expired signal and a same-named variable read as empty")]
    public async Task Only_a_set_signal_of_that_job_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var expiredJob = await SeedSlotJobAsync(ct);
        var variableJob = await SeedSlotJobAsync(ct);
        var pendingJob = await SeedSlotJobAsync(ct);
        await ParkAsync(pendingJob, OutboxSignalNames.Requeue, valueFormatId: 1, [9], NothingIsStale, ct);

        await ParkAsync(expiredJob, OutboxSignalNames.Requeue, valueFormatId: 1, [9], NothingIsStale, ct);
        Assert.Equal(
            1,
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == expiredJob && c.Kind == CheckpointKindCode.Signal && c.Name == OutboxSignalNames.Requeue)
                .UpdateOnlyAsync(() => new CheckpointEntity { Status = CheckpointStatusCode.Expired }, ct)
        );

        // A variable shares the checkpoints table and the composite key's job and name, differing only by kind.
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "INSERT INTO {schema}.checkpoints (job_id, kind_code, name, value_format_id) VALUES (@p_job_id, 10, @p_name, 0)",
                ct,
                ("@p_job_id", variableJob),
                ("@p_name", OutboxSignalNames.Requeue)
            )
        );

        Assert.Null(await GetAsync(expiredJob, OutboxSignalNames.Requeue, ct));
        Assert.Null(await GetAsync(variableJob, OutboxSignalNames.Requeue, ct));
        Assert.NotNull(await GetAsync(pendingJob, OutboxSignalNames.Requeue, ct));
    }

    private Task<OutboxSignalRow?> GetAsync(long jobId, string name, CancellationToken ct) =>
        Services.GetRequiredService<GetOutboxSignalHandler>().HandleAsync(new GetOutboxSignalQuery(jobId, name), ct);

    private Task<OutboxSignalAdmissionRow> ParkAsync(
        long jobId,
        string name,
        byte valueFormatId,
        byte[] value,
        DateTime staleBeforeUtc,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<ParkOutboxSignalHandler>()
            .HandleAsync(new ParkOutboxSignalCommand(jobId, name, valueFormatId, value, staleBeforeUtc), ct);

    private async Task<long> SeedSlotJobAsync(CancellationToken ct) => (await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct)).JobId;
}
