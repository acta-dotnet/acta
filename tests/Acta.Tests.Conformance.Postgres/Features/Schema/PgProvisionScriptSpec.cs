using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Postgres.Testing;
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
            // Twice on purpose: install and upgrade are the same file, so re-running it must apply
            // only what is missing. The second pass is what proves the header's promise, and the
            // migration-row count below is what proves it applied nothing the second time.
            for (var pass = 0; pass < 2; pass++)
            {
                await using var provision = conn.CreateCommand();
                provision.CommandText = script;
                await provision.ExecuteNonQueryAsync(ct);
            }

            // A body-only replacement must be repaired by bootstrap even with no pending MNNN.
            await using (var alter = conn.CreateCommand())
            {
                alter.CommandText = File.ReadAllText(
                        Path.Combine(
                            IntegrationConfig.FindRepoRoot(),
                            "src",
                            "Acta.Postgres",
                            "Sql",
                            "Services",
                            "Locks",
                            "ExtendLock.routine.sql"
                        )
                    )
                    .Replace("{{schema}}", schema)
                    .Replace("AS $$", "AS $$\n-- versionless_body_probe");
                await alter.ExecuteNonQueryAsync(ct);
            }
            await using (var definition = conn.CreateCommand())
            {
                definition.CommandText =
                    $"SELECT pg_get_functiondef(p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{schema}' AND p.proname = 'extend_lock'";
                Assert.Contains("versionless_body_probe", Assert.IsType<string>(await definition.ExecuteScalarAsync(ct)));
                await Acta.Postgres.Schema.PostgresSchemaMigrator.ApplyAsync(conn, schema, ct);
                Assert.DoesNotContain("versionless_body_probe", Assert.IsType<string>(await definition.ExecuteScalarAsync(ct)));
            }

            // One history row per migration section in the file plus the version-0 baseline-stamp
            // row, no more (the double run must not stamp anything twice), counted from the
            // script's own BEGIN banners.
            var migrations = BeginBanner().Matches(published).Count + 1;
            Assert.True(migrations > 1, "the published script contains no migration banners");

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {schema}.migrations), "
                + $"(SELECT COUNT(*) FROM {schema}.jobs_view), "
                + $"(SELECT COUNT(*) FROM information_schema.routines WHERE routine_schema = '{schema}')";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(migrations, reader.GetInt64(0));
            Assert.Equal(0, reader.GetInt64(1));
            Assert.Equal(57, reader.GetInt64(2));
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

    // The per-migration section banner the emitter writes above every migration.
    [GeneratedRegex(@"^-- ===== BEGIN M[0-9]{3}_", RegexOptions.Multiline)]
    private static partial Regex BeginBanner();
}
