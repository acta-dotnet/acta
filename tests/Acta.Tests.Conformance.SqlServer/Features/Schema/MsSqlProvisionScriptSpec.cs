using System.Text.RegularExpressions;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/schema-mssql.sql) must provision a working
/// schema when run verbatim (batch by GO batch, as sqlcmd/SSMS would), because DBA-run provisioning
/// under an elevated principal is exactly how locked-down deployments consume it. Runs the committed
/// file against a fresh schema name (using the header's own "replace the schema name throughout"
/// instruction) and inspects the result.
/// </summary>
public sealed partial class MsSqlProvisionScriptSpec
{
    [Fact(DisplayName = "The published mssql provision script provisions a fresh schema verbatim")]
    public async Task Published_script_provisions_a_fresh_schema()
    {
        var connString = IntegrationConfig.SqlServerConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_MSSQL is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var repoRoot = IntegrationConfig.FindRepoRoot();
        var published = File.ReadAllText(Path.Combine(repoRoot, "docs", "reference", "schema-mssql.sql"));
        var script = SchemaWord().Replace(published, schema);

        // On a fresh database the shared bootstrap flips READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK
        // IMMEDIATE, which kills every other session in the database. Await that bootstrap (the
        // serialization point every fixture-based spec already passes through) before opening the
        // raw connection, so this spec never races the bounce.
        await ActaSharedDatabase.EnsureReadyAsync(new SqlServerConformanceFixture());

        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync(ct);
        try
        {
            // Twice on purpose: the header promises that a re-run on a database the script provisioned
            // applies no migration twice, and the migration-row count below is what proves it.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var batch in SplitOnGo(script))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // A body-only replacement must be repaired by bootstrap even with no pending MNNN.
            var changedBody = File.ReadAllText(
                    Path.Combine(repoRoot, "src", "Acta.SqlServer", "Sql", "Services", "Locks", "ExtendLock.routine.sql")
                )
                .ReplaceLineEndings("\n")
                .Replace("{{schema}}", schema)
                .Replace("AS\nBEGIN", "AS\nBEGIN\n    -- versionless_body_probe");
            foreach (var batch in SplitOnGo(changedBody))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = batch;
                await alter.ExecuteNonQueryAsync(ct);
            }
            await using (var definition = conn.CreateCommand())
            {
                definition.CommandText = $"SELECT OBJECT_DEFINITION(OBJECT_ID('{schema}.extend_lock'))";
                Assert.Contains("versionless_body_probe", Assert.IsType<string>(await definition.ExecuteScalarAsync(ct)));
                await Acta.SqlServer.Schema.SqlServerSchemaMigrator.ApplyAsync(conn, schema, ct);
                Assert.DoesNotContain("versionless_body_probe", Assert.IsType<string>(await definition.ExecuteScalarAsync(ct)));
            }

            // One history row per migration section in the file plus the version-0 baseline-stamp
            // row, no more (the double run must not stamp anything twice), counted from the
            // script's own BEGIN banners, plus the version-0 baseline row and the object package row it records.
            var migrations = BeginBanner().Matches(published).Count + 2;
            Assert.True(migrations > 1, "the published script contains no migration banners");

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {schema}.migrations), "
                + $"(SELECT COUNT(*) FROM {schema}.jobs_view), "
                + $"(SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('{schema}'))";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(migrations, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(59, reader.GetInt32(2));
        }
        finally
        {
            await DropSchemaAsync(connString, repoRoot, schema);
        }
    }

    [Fact(DisplayName = "A mssql schema provisioned by the script runs with migrations disabled and keeps its data across a re-run")]
    public async Task Script_provisioned_schema_runs_and_keeps_its_data_across_a_rerun()
    {
        var connString = IntegrationConfig.SqlServerConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_MSSQL is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var repoRoot = IntegrationConfig.FindRepoRoot();
        var script = SchemaWord().Replace(File.ReadAllText(Path.Combine(repoRoot, "docs", "reference", "schema-mssql.sql")), schema);
        var fixture = new SqlServerConformanceFixture();
        await ActaSharedDatabase.EnsureReadyAsync(fixture);

        try
        {
            await RunScriptAsync(connString, script, ct);
            var job = await ScriptProvisionedRuntime.RunJobAsync(fixture.ApplyProvider, schema, 2, 3, ct);
            await RunScriptAsync(connString, script, ct);
            await ScriptProvisionedRuntime.AssertSucceededAsync(fixture.ApplyProvider, schema, job, 5, ct);
        }
        finally
        {
            await DropSchemaAsync(connString, repoRoot, schema);
        }
    }

    [Fact(DisplayName = "An older script over a newer release's package changes nothing, even in a client that runs on past errors")]
    public async Task An_older_script_over_a_newer_package_changes_nothing_when_errors_are_ignored()
    {
        var connString = IntegrationConfig.SqlServerConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_MSSQL is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_noexec_{Guid.NewGuid():N}"[..30];
        var repoRoot = IntegrationConfig.FindRepoRoot();
        var script = SchemaWord().Replace(File.ReadAllText(Path.Combine(repoRoot, "docs", "reference", "schema-mssql.sql")), schema);
        await ActaSharedDatabase.EnsureReadyAsync(new SqlServerConformanceFixture());
        try
        {
            await RunScriptAsync(connString, script, ct);

            // A newer release recorded its package, and one of its routines reads differently.
            await using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct);
            await using (var newer = conn.CreateCommand())
            {
                newer.CommandText = $"UPDATE {schema}.migrations SET name = 'objects-1.99-newer' WHERE version = -1";
                await newer.ExecuteNonQueryAsync(ct);
            }
            var marked = File.ReadAllText(
                    Path.Combine(repoRoot, "src", "Acta.SqlServer", "Sql", "Services", "Locks", "ExtendLock.routine.sql")
                )
                .ReplaceLineEndings("\n")
                .Replace("{{schema}}", schema)
                .Replace("AS\nBEGIN", "AS\nBEGIN\n    -- newer_release_body");
            foreach (var batch in SplitOnGo(marked))
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = batch;
                await alter.ExecuteNonQueryAsync(ct);
            }

            // Like sqlcmd without -b: every batch runs, whatever the one before it raised.
            var refused = false;
            foreach (var batch in SplitOnGo(script))
            {
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex) when (ex.Message.Contains("A newer Acta release", StringComparison.Ordinal))
                {
                    refused = true;
                }
            }

            Assert.True(refused, "the script did not refuse the newer package");
            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT OBJECT_DEFINITION(OBJECT_ID('{schema}.extend_lock')), (SELECT name FROM {schema}.migrations WHERE version = -1)";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Contains("newer_release_body", reader.GetString(0));
            Assert.Equal("objects-1.99-newer", reader.GetString(1));
        }
        finally
        {
            await DropSchemaAsync(connString, repoRoot, schema);
        }
    }

    private static async Task RunScriptAsync(string connString, string script, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync(ct);
        foreach (var batch in SplitOnGo(script))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// SQL Server has no DROP SCHEMA CASCADE; the provider's own teardown script drops the schema's
    /// routines, types, views, and tables in dependency order. It runs on its own connection: a
    /// provisioning failure can kill the test connection, and a teardown throw on the dead connection
    /// would mask the real error.
    /// </summary>
    private static async Task DropSchemaAsync(string connString, string repoRoot, string schema)
    {
        var teardown = File.ReadAllText(Path.Combine(repoRoot, "src", "Acta.SqlServer", "Sql", "Schema", "DropSchema.sql"))
            .Replace("{{schema}}", schema);
        await RunScriptAsync(connString, teardown, CancellationToken.None);
    }

    private static IEnumerable<string> SplitOnGo(string script) => GoLine().Split(script).Select(b => b.Trim()).Where(b => b.Length > 0);

    // A GO batch separator on its own line, the way sqlcmd and SSMS recognize it.
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline)]
    private static partial Regex GoLine();

    // The whole-word lowercase schema name, exactly what the script header tells a DBA to replace.
    [GeneratedRegex(@"\bacta\b")]
    private static partial Regex SchemaWord();

    // The per-migration section banner the emitter writes above every migration.
    [GeneratedRegex(@"^-- ===== BEGIN M[0-9]{3}_", RegexOptions.Multiline)]
    private static partial Regex BeginBanner();
}
