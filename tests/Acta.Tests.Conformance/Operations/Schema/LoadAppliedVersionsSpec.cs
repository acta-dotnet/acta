using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>LoadAppliedVersions</c>: the history the runner and the preflight judge a database by, read
/// outside a transaction by the preflight and inside the migration transaction by the runner.
/// </summary>
[ConformanceSpec(
    "load-applied-versions.history",
    "The migration history reads back version by version",
    Area = "Schema",
    Contract = "LoadAppliedVersions returns every recorded version with the name it was recorded under, with or without the migration transaction."
)]
public sealed class LoadAppliedVersionsSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "Every recorded version reads back with the name it was recorded under")]
    public async Task Reads_every_recorded_version()
    {
        var ct = TestContext.Current.CancellationToken;
        List<(int Version, string Name)> recorded = [(0, "baseline"), (1, "init"), (2, "add_widgets")];
        await using var probe = await Fixture.CreateHistoryProbeAsync(recorded);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        var history = await new LoadAppliedVersionsHandler(connection, sql, TimeoutSeconds).HandleAsync(
            new LoadAppliedVersionsQuery(null),
            ct
        );

        Assert.Equal(recorded, history.Versions.OrderBy(row => row.Key).Select(row => (row.Key, row.Value)));
    }

    [Fact(DisplayName = "Inside the migration transaction it reads the history table that transaction just created")]
    public async Task Reads_inside_the_migration_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await new EnsureMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new EnsureMigrationsCommand(tx), ct);

        var history = await new LoadAppliedVersionsHandler(connection, sql, TimeoutSeconds).HandleAsync(
            new LoadAppliedVersionsQuery(tx),
            ct
        );

        Assert.Empty(history.Versions);
        await tx.RollbackAsync(ct);
    }
}
