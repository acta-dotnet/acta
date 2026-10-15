using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>LoadAppliedMigrations</c>: the history the runner and the preflight judge a database by, read
/// outside a transaction by the preflight and inside the migration transaction by the runner.
/// </summary>
[ConformanceSpec(
    "load-applied-migrations.history",
    "The migration history reads back name by name",
    Area = "Schema",
    Contract = "LoadAppliedMigrations returns every recorded migration name, with or without the migration transaction, and fails on a schema without the history table."
)]
public sealed class LoadAppliedMigrationsSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "Every recorded migration reads back by name")]
    public async Task Reads_every_recorded_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        List<string> recorded = ["M20260101000000_init", "M20260102000000_add_widgets"];
        await using var probe = await Fixture.CreateHistoryProbeAsync(recorded);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        var history = await new LoadAppliedMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(
            new LoadAppliedMigrationsQuery(null),
            ct
        );

        Assert.Equal(recorded, history.Names.Order(StringComparer.Ordinal));
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

        var history = await new LoadAppliedMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(
            new LoadAppliedMigrationsQuery(tx),
            ct
        );

        Assert.Empty(history.Names);
        await tx.RollbackAsync(ct);
    }

    [Fact(DisplayName = "Without a history table the read fails with the provider's error")]
    public async Task Fails_without_the_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            new LoadAppliedMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new LoadAppliedMigrationsQuery(null), ct)
        );
    }
}
