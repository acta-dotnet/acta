using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// Rows staged into a lane in sequence keep their staging order from the outbox to the ledger. A laned row
/// is claimed only while no older row of its lane is still Pending or Claimed, so a rejected row holds the
/// rows behind it, a claim another relay holds does too, and a quarantined row lets its lane move on. Other
/// lanes flow. Producers open at the same time are <c>outbox.lane-producers</c>.
/// </summary>
[ConformanceSpec(
    "outbox.lane-hold",
    "An outbox lane waits behind its oldest waiting row until that row leaves",
    Area = "Outbox",
    Contract = "A laned outbox row is claimed only while no older row of its lane is Pending or Claimed, so a lane staged in sequence reaches the ledger in staging order."
)]
public sealed class OutboxLaneHoldSpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "A rejected row holds its lane across ticks while another lane relays")]
    public async Task A_rejected_row_holds_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(
            SourceTable,
            [Row("held", "a", jobName: "no-such-job"), Row("held", "b"), Row("free", "c")]
        );

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        await Fixture.RewindOutboxAsync(SourceTable);
        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(1, await CountLedgerJobsAsync(Key("c"), ct));
        Assert.Equal(0, await CountLedgerJobsAsync(Key("b"), ct));
        Assert.Equal(2, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A quarantined row releases its lane and the row behind it relays")]
    public async Task A_quarantined_row_releases_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("dead", "a", jobName: "no-such-job"), Row("dead", "b")]);

        await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(quarantineThreshold: 1), ct)
        );

        Assert.Equal(1, await CountLedgerJobsAsync(Key("b"), ct));
    }

    [Fact(DisplayName = "A lane's later rows wait while another claim holds its head")]
    public async Task A_claimed_head_holds_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("claimed", "a"), Row("claimed", "b")]);

        // Another relay's claim, left in flight: the head is Claimed under a token this tick does not own.
        var held = (await SourceStore.ClaimDueAsync(new ClaimOutboxCommand(Guid.NewGuid(), 10, 180), ct)).Rows;
        Assert.Single(held);

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(0, await CountLedgerJobsAsync(Key("a"), ct));
        Assert.Equal(0, await CountLedgerJobsAsync(Key("b"), ct));
    }

    [Theory(DisplayName = "A lane's later rows wait while another relay moves its head, and stay held when that move rolls back")]
    [InlineData("RescheduleOutbox")]
    [InlineData("ReleaseClaimedOutbox")]
    [InlineData("QuarantineOutbox")]
    public async Task A_head_being_moved_holds_its_lane(string finalize)
    {
        Assert.SkipWhen(
            Fixture.DialectToken == "sqlite",
            "SQLite runs one writer at a time, so a finalize cannot stay open beside a tick."
        );
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("moving", "a"), Row("moving", "b")]);
        var token = Guid.NewGuid();
        var head = Assert.Single((await SourceStore.ClaimDueAsync(new ClaimOutboxCommand(token, 10, 180), ct)).Rows);

        // Another relay's finalize of the head, held open and then rolled back, so the head stays Claimed throughout.
        var session = SourceConnection.Session;
        var dialect = SourceConnection.Dialect;
        await using var connection = await session.OpenConnectionAsync(ct);
        await using var move = await connection.BeginTransactionAsync(ct);
        await session.ExecuteInTransactionAsync(
            move,
            new StoreCommand(finalize),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, token));
                cmd.Parameters.Add(
                    finalize switch
                    {
                        "RescheduleOutbox" => dialect.CreateParameter(
                            OutboxSchema.Sql.RowRecords,
                            OutboxJson.Reschedules([new OutboxReschedule(head.OutboxId, 1, 3600, "held open")])
                        ),
                        "QuarantineOutbox" => dialect.CreateParameter(
                            OutboxSchema.Sql.RowRecords,
                            OutboxJson.Quarantines([new OutboxQuarantine(head.OutboxId, 1, "held open")])
                        ),
                        _ => dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, OutboxJson.IdArray([head.OutboxId])),
                    }
                );
            },
            _ => 0,
            ct
        );

        // SQL Server's claim waits for the finalize's outbox lock; PostgreSQL's reads the head's committed claim and
        // finishes without waiting. Either way the row behind the head must not relay.
        var tick = Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        if (session.Provider == DbProvider.SqlServer)
        {
            await LockWaitProbe.WaitAsync(session, "@p_batch_size", LockWait.ApplicationLock, tick, move, ct);
        }
        else
        {
            await tick;
        }

        await move.RollbackAsync(ct);
        await tick;
        Assert.Equal(0, await CountLedgerJobsAsync(Key("b"), ct));
    }

    [Fact(DisplayName = "A long lane lands in the ledger in staging order")]
    public async Task A_long_lane_lands_in_staging_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var labels = Enumerable.Range(0, 30).Select(i => $"n{i:D2}").ToList();
        await Fixture.StageInOneTransactionAsync(SourceTable, [.. labels.Select(l => Row("long", l))]);

        // A tick advances a lane one row per claim, twenty claims at most.
        for (var tick = 0; tick < 3 && await Fixture.CountOutboxAsync(SourceTable) > 0; tick++)
        {
            await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        }

        var keys = labels.Select(Key).ToList();
        var jobs = await Db.From<JobEntity>().Where(j => j.NamespaceId == NamespaceId).ToListAsync(ct);
        Assert.Equal(keys, jobs.Where(j => keys.Contains(j.DeduplicationKey!)).OrderBy(j => j.Id).Select(j => j.DeduplicationKey));
    }

    private string Key(string label) => $"{label}-{TestId}";

    private EnqueueJobRequest Row(string lane, string label, string jobName = "lane-step") =>
        new(TestNamespace, jobName, JobPayload.Json(new LaneStep(lane, label)), DeduplicationKey: Key(label), Lane: $"{lane}-{TestId}");
}
