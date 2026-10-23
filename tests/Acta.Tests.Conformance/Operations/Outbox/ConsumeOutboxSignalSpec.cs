using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the delete that closes an applied operator command on a <c>sys.outbox</c> slot and writes its
/// evidence event, the one trail for rows that live, or lived, in the producer's database.
/// </summary>
[ConformanceSpec(
    "consume-outbox-signal.command-identity",
    "Consume deletes only the command the tick read and writes its one evidence event",
    Area = "Outbox",
    Contract = "ConsumeOutboxSignal deletes a signal row only while it holds the value the tick read and only then writes its evidence event, so a repeat changes nothing."
)]
public sealed class ConsumeOutboxSignalSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Reason = "poison batch - 2 row(s): [a, b]";

    private static readonly DateTime NothingIsStale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EverythingIsStale = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private int DefId;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "outbox-slot-def", ct: TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "A consume of the command read deletes the row and answers true, and a repeat answers false")]
    public async Task Consume_of_the_command_read_deletes_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);

        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, [1], ct));
        Assert.Equal(0, await CountAsync(jobId, OutboxSignalNames.Requeue, ct));

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, [1], ct));
    }

    [Fact(DisplayName = "A consume of a superseded command answers false and the newer command survives")]
    public async Task Consume_of_a_superseded_command_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] newer = [2];
        await ParkAsync(jobId, OutboxSignalNames.Discard, [1], NothingIsStale, ct);
        await ParkAsync(jobId, OutboxSignalNames.Discard, newer, EverythingIsStale, ct);

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Discard, [1], ct));

        var survivor = Assert.Single(await RowsAsync(jobId, OutboxSignalNames.Discard, ct));
        Assert.Equal(newer, survivor.Value);
        Assert.Equal(1, survivor.Version);
    }

    [Fact(DisplayName = "A late consume of a command another pass consumed misses the command parked after it at the same version")]
    public async Task Late_consume_misses_a_command_parked_at_the_same_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        byte[] first = [1];
        byte[] parkedAfter = [2];
        await ParkAsync(jobId, OutboxSignalNames.Requeue, first, NothingIsStale, ct);
        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, first, ct));

        Assert.Equal(1, (await ParkAsync(jobId, OutboxSignalNames.Requeue, parkedAfter, NothingIsStale, ct)).Action);
        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, first, ct));

        var survivor = Assert.Single(await RowsAsync(jobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal(parkedAfter, survivor.Value);
        Assert.Equal(0, survivor.Version);
    }

    [Fact(DisplayName = "A consume of an empty slot answers false")]
    public async Task Consume_of_an_empty_slot_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);

        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, [1], ct));
    }

    [Fact(DisplayName = "A consume deletes only the named command of that job")]
    public async Task Consume_is_scoped_to_one_job_and_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        var otherJobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);
        await ParkAsync(jobId, OutboxSignalNames.Discard, [1], NothingIsStale, ct);
        await ParkAsync(otherJobId, OutboxSignalNames.Requeue, [1], NothingIsStale, ct);

        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Requeue, [1], ct));

        Assert.Equal(0, await CountAsync(jobId, OutboxSignalNames.Requeue, ct));
        Assert.Equal(1, await CountAsync(jobId, OutboxSignalNames.Discard, ct));
        Assert.Equal(1, await CountAsync(otherJobId, OutboxSignalNames.Requeue, ct));
    }

    [Fact(DisplayName = "A consume writes one evidence event with the operator and reason, and a repeat writes none")]
    public async Task Consume_writes_one_evidence_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await SeedSlotJobAsync(ct);
        await ParkAsync(jobId, OutboxSignalNames.Discard, [1], NothingIsStale, ct);

        Assert.True(await ConsumeAsync(jobId, OutboxSignalNames.Discard, [1], ct));
        Assert.False(await ConsumeAsync(jobId, OutboxSignalNames.Discard, [1], ct));

        // The seeded job records no events, which proves the evidence event has no recording gate.
        var evidence = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId).ToListAsync(ct)
        );
        Assert.Equal(EventCode.OutboxDiscarded, evidence.EventCode);
        Assert.Equal("marko", EventDetail(evidence, "actor"));
        Assert.Equal(Reason, evidence.EventMessage);
        Assert.Equal(1, evidence.ExecutionNumber);
    }

    private async Task<bool> ConsumeAsync(long jobId, string name, byte[] value, CancellationToken ct) =>
        (
            await Services
                .GetRequiredService<ConsumeOutboxSignalHandler>()
                .HandleAsync(
                    new ConsumeOutboxSignalCommand(
                        jobId,
                        name,
                        value,
                        name == OutboxSignalNames.Requeue ? EventCode.OutboxRequeued : EventCode.OutboxDiscarded,
                        "marko",
                        Reason
                    ),
                    ct
                )
        ).Consumed;

    private Task<ParkOutboxSignalResponse> ParkAsync(
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
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.CheckpointName == name)
            .ToListAsync(ct);

    private async Task<int> CountAsync(long jobId, string name, CancellationToken ct) => (await RowsAsync(jobId, name, ct)).Count;

    // The jobs_state row backs the evidence event's join, as the real slot's does.
    private async Task<long> SeedSlotJobAsync(CancellationToken ct)
    {
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
        await Db.From<JobStateEntity>()
            .InsertAsync<long>(
                new JobStateEntity
                {
                    Id = jobId,
                    NamespaceId = TestNamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    // Ready carries its due instant the way register_schedules writes the real
                    // slot row; ck_jobs_state_ready_due rejects the pair otherwise.
                    NextRunAtUtc = DateTime.UtcNow,
                    ExecutionNumber = 1,
                },
                ct
            );
        return jobId;
    }
}
