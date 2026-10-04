using System.Data.Common;
using Acta.Runtime;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Pins <see cref="DefinitionKeyResolution"/>'s second defense, the ordinal name check: the session here
/// over-matches, returning every sibling for any query, so only that check can keep the answer right. A
/// prefix or suffix sibling must never resolve in another's place - picking the wrong row would edit or
/// read the wrong definition.
/// </summary>
public sealed class DefinitionKeyResolutionTests
{
    private const string Namespace = "billing";

    private static DefinitionListItem Row(int id, string name) =>
        new(
            DefinitionId: id,
            JobNamespace: Namespace,
            JobName: name,
            Status: DefinitionStatusCode.Active,
            InputTypeName: "Input",
            OutputTypeName: null,
            PriorityOverride: null,
            PriorityEffective: JobPriorityCode.Normal,
            MaxAttemptsOverride: null,
            MaxAttemptsEffective: 3,
            ConcurrencyLimitOverride: null,
            ConcurrencyLimitEffective: null,
            RateLimitOverride: null,
            RateLimitEffective: null,
            RateKey: null,
            Lane: null,
            ModifiedAtUtc: new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc),
            Version: 1
        );

    // invoice sits between a suffix sibling (invoice-retry) and a prefix sibling (bulk-invoice), so a
    // resolver that over- or under-matched in either direction lands on the wrong row.
    private static readonly DefinitionListItem[] Catalog = [Row(1, "invoice"), Row(2, "invoice-retry"), Row(3, "bulk-invoice")];

    private static DefinitionKeyResolution Keys(IDbSession session) => new(session, null!);

    [Theory]
    [InlineData("invoice", 1)]
    [InlineData("invoice-retry", 2)]
    [InlineData("bulk-invoice", 3)]
    public async Task A_name_resolves_to_its_own_definition_even_when_the_read_over_matches(string jobName, int expectedId)
    {
        var id = await Keys(new OverMatchingSession(Catalog)).ResolveAsync(Namespace, jobName, TestContext.Current.CancellationToken);

        Assert.Equal(expectedId, id);
    }

    [Fact]
    public async Task A_name_no_definition_carries_resolves_to_null_rather_than_a_sibling()
    {
        var keys = Keys(new OverMatchingSession(Catalog));
        var ct = TestContext.Current.CancellationToken;

        // "invoic" is a prefix of a registered name and "invoice-retry-2" extends one; neither exists.
        Assert.Null(await keys.ResolveAsync(Namespace, "invoic", ct));
        Assert.Null(await keys.ResolveAsync(Namespace, "invoice-retry-2", ct));
    }

    [Fact]
    public async Task An_override_write_addressed_by_a_sibling_prefix_is_not_found_and_writes_nothing()
    {
        // The stub session throws on any write, so an answer at all proves nothing was written.
        var session = new OverMatchingSession(Catalog);
        var handler = new SetJobDefinitionOverridesHandler(Keys(session), session, null!);

        var outcome = await handler.HandleAsync(
            new SetJobDefinitionOverridesCommand(
                Namespace,
                "invoic",
                ExpectedVersion: 1,
                new DefinitionPolicyOverrides(MaxAttempts: 9),
                ReasonMessage: "typo",
                ActorKey: "tester"
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ControlAction.NotFound, outcome.Action);
    }

    /// <summary>
    /// A session whose list read ignores the name filter entirely and returns the whole catalog. Nothing
    /// but the resolver's own ordinal match can produce the right row, which is exactly what is pinned.
    /// </summary>
    private sealed class OverMatchingSession(IReadOnlyList<DefinitionListItem> rows) : DbSessionStub
    {
        public override Task<T> QueryAsync<T>(
            string stem,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        )
        {
            (IReadOnlyList<DefinitionListItem> Rows, long? Total) page = (rows, rows.Count);
            return Task.FromResult((T)(object)page);
        }
    }
}
