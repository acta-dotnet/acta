using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schema;

/// <summary>
/// Conformance for <c>MigrationsTableExists</c>: the preflight's first question, which must answer for a database that
/// was never provisioned instead of failing on it.
/// </summary>
[ConformanceSpec(
    "migrations-table-exists.probe",
    "The history-table probe answers whether the table is there",
    Area = "Schema",
    Contract = "MigrationsTableExists answers false for a schema without a migration-history table and true for one with it, failing on neither."
)]
public sealed class MigrationsTableExistsSpec : IntegrationSpec<ConformanceFixture>
{
    private const int TimeoutSeconds = 30;

    [Fact(DisplayName = "False without a history table, true with an empty one")]
    public async Task Answers_both_ways()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await ExistsAsync(history: null, ct));
        Assert.True(await ExistsAsync(history: [], ct));
    }

    private async Task<bool> ExistsAsync(IReadOnlyList<(int Version, string Name)>? history, CancellationToken ct)
    {
        await using var probe = await Fixture.CreateHistoryProbeAsync(history);
        var sql = new SqlResourceCatalog(Fixture.DialectToken, probe.SchemaName);
        await using var connection = await Fixture.OpenConnectionAsync(probe.ConnectionString, ct);
        return (await new MigrationsTableExistsHandler(connection, sql, TimeoutSeconds).HandleAsync(ct)).Exists;
    }
}
