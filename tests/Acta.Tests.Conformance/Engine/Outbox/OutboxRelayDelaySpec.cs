using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Operations.Outbox;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// Conformance for a staged delay. The relay hands a staged <c>delay_seconds</c> to the enqueue, which resolves it on
/// the ledger's clock as it inserts the job, so the delay counts from the relay's ingest however long the row waited.
/// </summary>
[ConformanceSpec(
    "outbox.relay-delay",
    "A staged delay counts from the relay's ingest",
    Area = "Outbox",
    Contract = "A staged DelaySeconds is resolved on the ledger's clock when the relay enqueues the job, not from when the row was staged."
)]
public sealed class OutboxRelayDelaySpec : OutboxRelayHostBase
{
    private const int DelaySeconds = 3600;

    [Fact(DisplayName = "A row staged two hours ago with an hour's delay runs an hour after the relay ingests it")]
    public async Task Staged_delay_counts_from_ingest()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("delayed");
        var staged = DateTime.UtcNow.AddHours(-2);
        await Fixture.SeedOutboxRowAsync(
            SourceTable,
            new OutboxSeed(
                OutboxId: Guid.NewGuid(),
                JobNamespace: TestNamespace,
                JobName: "job-wait-signal",
                InputFormatId: 0,
                Input: null,
                DeduplicationKey: key,
                PriorityCode: null,
                CreatedAtUtc: staged,
                NextAttemptAtUtc: staged,
                StatusCode: (byte)OutboxStatusCode.Pending,
                FailureCount: 0
            )
        );
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                $"UPDATE {{schema}}.{SourceTable} SET delay_seconds = @p_delay WHERE deduplication_key = @p_key",
                ct,
                ("@p_delay", DelaySeconds),
                ("@p_key", key)
            )
        );

        var before = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        await RunRelayPassAsync(ct);
        var after = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        // Counted from the staging, the job would have been due an hour ago.
        var job = Assert.Single(await Db.From<JobEntity>().Where(j => j.DeduplicationKey == key).ToListAsync(ct));
        var state = await ReadJobAsync(job.Id, ct);
        Assert.NotNull(state.JobState.NextRunAtUtc);
        Assert.InRange(state.JobState.NextRunAtUtc.Value, before.AddSeconds(DelaySeconds - 1), after.AddSeconds(DelaySeconds + 1));
    }
}
