using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>EnsureMigrations</c>: every bootstrap runs it, so it creates the history table once and never
/// touches one that is there.
/// </summary>
[ConformanceSpec(
    "ensure-migrations.create-once",
    "The migration-history table is created once and keeps its rows",
    Area = "Schema",
    Contract = "EnsureMigrations creates the migration-history table inside the migration transaction when it is missing, and leaves an existing table and its rows alone."
)]
public sealed class EnsureMigrationsSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "Where there is no history table it creates an empty one")]
    public async Task Creates_a_missing_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await new EnsureMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new EnsureMigrationsCommand(tx), ct);
            await tx.CommitAsync(ct);
        }

        Assert.Empty(await LoadAsync(connection, sql, ct));
    }

    [Fact(DisplayName = "Over an existing history it keeps every recorded row")]
    public async Task Keeps_an_existing_history()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] recorded = ["M20260101000000_init", "M20260102000000_add_widgets"];
        await using var probe = await Fixture.CreateHistoryProbeAsync(recorded);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await new EnsureMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new EnsureMigrationsCommand(tx), ct);
            await tx.CommitAsync(ct);
        }

        Assert.Equal(recorded, (await LoadAsync(connection, sql, ct)).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "Rolled back with the migration transaction, the table it created is gone")]
    public async Task Rolls_back_with_its_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);

        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await new EnsureMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new EnsureMigrationsCommand(tx), ct);
            await tx.RollbackAsync(ct);
        }

        await Assert.ThrowsAnyAsync<DbException>(() => LoadAsync(connection, sql, ct));
    }

    private static async Task<IReadOnlySet<string>> LoadAsync(DbConnection connection, SqlResourceCatalog sql, CancellationToken ct) =>
        (
            await new LoadAppliedMigrationsHandler(connection, sql, TimeoutSeconds).HandleAsync(new LoadAppliedMigrationsQuery(null), ct)
        ).Names;
}
