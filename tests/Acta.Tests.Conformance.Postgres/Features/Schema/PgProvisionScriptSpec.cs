using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/provision/pg.sql) must provision a working
/// schema when run verbatim, because DBA-run provisioning under an elevated principal is exactly how
/// locked-down deployments consume it. Runs the committed file against a fresh schema name (using
/// the header's own "replace the schema name throughout" instruction) and inspects the result.
/// </summary>
public sealed partial class PgProvisionScriptSpec
{
    [Fact(DisplayName = "The published pg provision script provisions a fresh schema verbatim")]
    public async Task Published_script_provisions_a_fresh_schema()
    {
        var connString = IntegrationConfig.PostgresConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_PG is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var published = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "provision", "pg.sql"));
        var script = SchemaWord().Replace(published, schema);

        // The shared bootstrap owns creating the test database; a clean CI runner has none until it
        // runs. Await it (the serialization point every fixture-based spec already passes through)
        // before opening the raw connection, so this spec never races or precedes that creation.
        await ActaSharedDatabase.EnsureReadyAsync(new PgConformanceFixture());

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            await using (var provision = conn.CreateCommand())
            {
                provision.CommandText = script;
                await provision.ExecuteNonQueryAsync(ct);
            }

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {schema}.migrations WHERE version = 1), "
                + $"(SELECT COUNT(*) FROM {schema}.jobs_view), "
                + $"(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema = '{schema}')";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.True(reader.GetInt64(2) > 0, "no routines were installed");
        }
        finally
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // The whole-word lowercase schema name, exactly what the script header tells a DBA to replace.
    [GeneratedRegex(@"\bacta\b")]
    private static partial Regex SchemaWord();
}
