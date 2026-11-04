using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>GetOutboxTable</c>: the outbox install's probe, which decides whether the install runs the CREATE. Only
/// a table counts, so another kind of object with the name fails that CREATE instead of reading as the table.
/// </summary>
[ConformanceSpec(
    "get-outbox-table.tables-only",
    "The outbox install's probe finds a table and nothing else",
    Area = "Schema",
    Contract = "GetOutboxTable answers true for an existing table of the catalog's name, and false for a missing name or a view that carries it."
)]
public sealed class GetOutboxTableSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;
    private const string Table = "acta_outbox";

    [Fact(DisplayName = "A missing name is not found, and the table is once it exists")]
    public async Task Finds_the_table_once_it_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);

        Assert.False(await FindAsync(probe, ct));
        await ExecuteAsync(probe, $"CREATE TABLE {probe.SchemaName}.{Table} (id integer NOT NULL);", ct);
        Assert.True(await FindAsync(probe, ct));
    }

    [Fact(DisplayName = "A view with the table's name is not the table")]
    public async Task A_view_is_not_the_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var probe = await Fixture.CreateHistoryProbeAsync(history: null);

        await ExecuteAsync(probe, $"CREATE VIEW {probe.SchemaName}.{Table} AS SELECT 1 AS id;", ct);
        Assert.False(await FindAsync(probe, ct));
    }

    private Task<bool> FindAsync(IMigrationHistoryProbe probe, CancellationToken ct) =>
        TestDdl.RunAsync(async () =>
        {
            await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName, Table);
            var found = await new GetOutboxTableHandler(connection, sql, TimeoutSeconds).HandleAsync(new GetOutboxTableQuery(tx), ct);
            return found.Exists;
        });

    private Task ExecuteAsync(IMigrationHistoryProbe probe, string ddl, CancellationToken ct) =>
        TestDdl.RunAsync(async () =>
        {
            await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = ddl;
            await command.ExecuteNonQueryAsync(ct);
        });
}
