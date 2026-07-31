using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the provider <c>AddToActaOutboxAsync</c> staging extension: a business write and the
/// staged outbox row share the caller's own transaction, so they commit together and roll back together,
/// the database defaults populate the operational columns, and the staged row is immediately claimable
/// and reconstructable by the real relay store. Provider-native transaction plumbing lives in the fixture.
/// </summary>
[ConformanceSpec(
    "outbox-staging.commit-rollback-defaults",
    "Provider outbox staging commits or rolls back with the business write",
    Area = "Outbox",
    Contract = "The staging extension writes one canonical outbox row on the caller transaction, so it commits or rolls back with the business write and is then claimable.",
    Arrange = "A per-test outbox table exists and a request carries a payload, correlation key, priority, and tags.",
    Act = "A business row and the staged outbox row are written on one caller transaction, then committed or rolled back.",
    Assert = "On commit both rows persist and the staged row claims once with failure count zero and reconstructs the request, on rollback neither row exists."
)]
public abstract class OutboxStagingSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A committed stage persists the business row and a claimable, reconstructable outbox row")]
    public async Task Commit_persists_and_row_is_claimable()
    {
        var ct = TestContext.Current.CancellationToken;
        var table = "acta_outbox_stage_" + TestId;
        await Fixture.ApplyOutboxDdlAsync(table);
        var request = Request();

        var (business, outbox) = await Fixture.StageWithBusinessWriteAsync(table, request, commit: true);
        Assert.Equal(1, business);
        Assert.Equal(1, outbox);

        var store = (IOutboxRelayStore)Fixture.CreateOutboxStore(table);
        var claimed = await store.ClaimDueAsync(new ClaimOutboxCommand(Guid.NewGuid(), 10, 180), ct);
        var row = Assert.Single(claimed);
        Assert.Equal(0, row.FailureCount);

        var rebuilt = OutboxRequestReconstruction.ToRequest(row, 1 << 20);
        Assert.Equal(request.JobNamespace, rebuilt.JobNamespace);
        Assert.Equal(request.JobName, rebuilt.JobName);
        Assert.Equal(request.DeduplicationKey, rebuilt.DeduplicationKey);
        Assert.Equal(request.CorrelationKey, rebuilt.CorrelationKey);
        Assert.Equal(request.Priority, rebuilt.Priority);
        Assert.Equal(request.Input.Format.Id, rebuilt.Input.Format.Id);
        Assert.Equal(request.Input.Data.ToArray(), rebuilt.Input.Data.ToArray());
        Assert.NotNull(rebuilt.Tags);
        Assert.Collection(
            rebuilt.Tags!,
            t => Assert.Equal(("tenant", "acme"), (t.Name, t.Value)),
            t => Assert.Equal(("urgent", (string?)null), (t.Name, t.Value))
        );
    }

    [Fact(DisplayName = "A rolled-back stage discards both the business row and the outbox row")]
    public async Task Rollback_discards_both()
    {
        var table = "acta_outbox_stage_" + TestId;
        await Fixture.ApplyOutboxDdlAsync(table);

        var (business, outbox) = await Fixture.StageWithBusinessWriteAsync(table, Request(), commit: false);
        Assert.Equal(0, business);
        Assert.Equal(0, outbox);
    }

    private JobEnqueueRequest Request() =>
        new(
            "orders",
            "send-receipt",
            JobPayload.CopyBytes(JobPayloadFormat.Json, "{\"a\":1}"u8),
            DeduplicationKey: "stage-" + TestId,
            CorrelationKey: "corr-1",
            Priority: JobPriorityCode.High,
            Tags: [new TagInput("tenant", "acme"), new TagInput("urgent", null)]
        );
}
