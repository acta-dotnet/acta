using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the rewrite that stores an operator command's plan in the command's own row.</summary>
[ConformanceSpec(
    "plan-outbox-signal.command-identity",
    "Plan replaces only the command the tick read with its plan",
    Area = "Outbox",
    Contract = "PlanOutboxSignal replaces a signal row with the planned value only while it holds the value the tick read, so a pass that read an older value stores nothing."
)]
public sealed class PlanOutboxSignalSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime NothingIsStale = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "A plan of the command read replaces it and answers true, and a plan from the old read answers false")]
    public async Task Plan_of_the_command_read_replaces_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);
        byte[] read = [1];
        byte[] plan = [1, 2];
        await ParkAsync(jobId, read, ct);

        Assert.True(await PlanAsync(jobId, read, plan, ct));
        Assert.False(await PlanAsync(jobId, read, [1, 3], ct));

        var stored = Assert.Single(await RowsAsync(jobId, ct));
        Assert.Equal(plan, stored.Value);
    }

    [Fact(DisplayName = "A plan of an empty slot answers false and stores nothing")]
    public async Task Plan_of_an_empty_slot_misses()
    {
        var ct = TestContext.Current.CancellationToken;
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);

        Assert.False(await PlanAsync(jobId, [1], [1, 2], ct));
        Assert.Empty(await RowsAsync(jobId, ct));
    }

    private async Task<bool> PlanAsync(long jobId, byte[] value, byte[] plannedValue, CancellationToken ct) =>
        (
            await Services
                .GetRequiredService<PlanOutboxSignalHandler>()
                .HandleAsync(new PlanOutboxSignalCommand(jobId, OutboxSignalNames.Requeue, value, plannedValue), ct)
        ).Planned;

    private Task<ParkOutboxSignalResponse> ParkAsync(long jobId, byte[] value, CancellationToken ct) =>
        Services
            .GetRequiredService<ParkOutboxSignalHandler>()
            .HandleAsync(new ParkOutboxSignalCommand(jobId, OutboxSignalNames.Requeue, ValueFormatId: 1, value, NothingIsStale), ct);

    private async Task<IReadOnlyList<CheckpointEntity>> RowsAsync(long jobId, CancellationToken ct) =>
        await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.CheckpointName == OutboxSignalNames.Requeue)
            .ToListAsync(ct);
}
