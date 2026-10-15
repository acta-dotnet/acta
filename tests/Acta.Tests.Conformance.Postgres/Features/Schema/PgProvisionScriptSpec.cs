using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Sql;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/schema-pg.sql) must provision a working
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
        var published = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-pg.sql"));
        var script = SchemaWord().Replace(published, schema);

        // The shared bootstrap owns creating the test database; a clean CI runner has none until it
        // runs. Await it (the serialization point every fixture-based spec already passes through)
        // before opening the raw connection, so this spec never races or precedes that creation.
        await ActaSharedDatabase.EnsureReadyAsync(new PgConformanceFixture());

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            // Twice on purpose: the header promises that a re-run on a database the script provisioned
            // applies no migration twice, and the migration-row count below is what proves it.
            for (var pass = 0; pass < 2; pass++)
            {
                await using var provision = conn.CreateCommand();
                provision.CommandText = script;
                await provision.ExecuteNonQueryAsync(ct);
            }

            // One history row per migration section in the file, no more (the double run must not
            // stamp anything twice), counted from the script's own BEGIN banners.
            var migrations = BeginBanner().Matches(published).Count;
            Assert.True(migrations > 0, "the published script contains no migration banners");

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {schema}.migrations), "
                + $"(SELECT COUNT(*) FROM {schema}.jobs), "
                + $"(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema = '{schema}')";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(migrations, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(14, reader.GetInt64(2));
        }
        finally
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "A pg schema provisioned by the script runs with migrations disabled and keeps its data across a re-run")]
    public async Task Script_provisioned_schema_runs_and_keeps_its_data_across_a_rerun()
    {
        var connString = IntegrationConfig.PostgresConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_PG is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var script = SchemaWord()
            .Replace(File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-pg.sql")), schema);
        var fixture = new PgConformanceFixture();
        await ActaSharedDatabase.EnsureReadyAsync(fixture);

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            await ExecuteAsync(conn, script, ct);
            var job = await ScriptProvisionedRuntime.RunJobAsync(fixture.ApplyProvider, schema, 2, 3, ct);
            await ExecuteAsync(conn, script, ct);
            await ScriptProvisionedRuntime.AssertSucceededAsync(fixture.ApplyProvider, schema, job, 5, ct);
        }
        finally
        {
            // Its own connection, because a failed script leaves the test connection inside an aborted transaction.
            await using var cleanup = new NpgsqlConnection(connString);
            await cleanup.OpenAsync(CancellationToken.None);
            await ExecuteAsync(cleanup, $"DROP SCHEMA IF EXISTS {schema} CASCADE;", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // The whole-word lowercase schema name, exactly what the script header tells a DBA to replace.
    [GeneratedRegex(@"\bacta\b")]
    private static partial Regex SchemaWord();

    // The per-migration section banner the emitter writes above every migration.
    [GeneratedRegex(@"^-- ===== BEGIN M[0-9]{3}_", RegexOptions.Multiline)]
    private static partial Regex BeginBanner();
}
