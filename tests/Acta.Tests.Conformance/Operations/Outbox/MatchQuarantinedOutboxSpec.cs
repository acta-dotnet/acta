using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the read that resolves an operator command into the rows it targets before it changes any: the
/// Quarantined rows among the ids it names, or every Quarantined row when it names none, and nothing it changes.
/// </summary>
[ConformanceSpec(
    "outbox-match.command-targets",
    "Match reads the Quarantined rows a command targets without changing them",
    Area = "Outbox",
    Contract = "MatchQuarantined returns the Quarantined rows among the given ids, or every Quarantined row when ids are null, and changes no row."
)]
public sealed class MatchQuarantinedOutboxSpec : OutboxSpecBase
{
    [Fact(DisplayName = "An id-scoped match returns only its quarantined ids, the null form returns every one, and no row moves")]
    public async Task Match_returns_targeted_quarantined_rows_and_changes_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAndQuarantineAsync(Unique("mq-a"), "poison a", ct);
        var second = await SeedAndQuarantineAsync(Unique("mq-b"), "poison b", ct);
        var pending = OutboxSpecBase.DueRow(Unique("mq-c"));
        await Fixture.SeedOutboxRowAsync(TableName, pending);

        var scoped = await Store.MatchQuarantinedAsync(new MatchQuarantinedOutboxQuery([first, pending.OutboxId]), ct);
        Assert.Equal([first], scoped.OutboxIds);
        Assert.Equal(
            new HashSet<Guid> { first, second },
            (await Store.MatchQuarantinedAsync(new MatchQuarantinedOutboxQuery(null), ct)).OutboxIds.ToHashSet()
        );

        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(TableName, first)).StatusCode);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(TableName, second)).StatusCode);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(TableName, pending.OutboxId)).StatusCode);
    }
}
