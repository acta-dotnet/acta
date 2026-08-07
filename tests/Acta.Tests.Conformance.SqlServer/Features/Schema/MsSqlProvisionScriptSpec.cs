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
            // Twice on purpose: install and upgrade are the same file, so re-running it must apply
            // only what is missing. The second pass is what proves the header's promise, and the
            // migration-row count below is what proves it applied nothing the second time.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var batch in SplitOnGo(script))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
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
                + $"(SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('{schema}'))";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(migrations, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.True(reader.GetInt32(2) > 0, "no routines were installed");
        }
        finally
        {
            // SQL Server has no DROP SCHEMA CASCADE; the provider's own teardown script drops the
            // schema's routines, types, views, and tables in dependency order. It runs on its own
            // connection: a provisioning failure can kill the test connection, and a teardown throw
            // on the dead connection would mask the real error.
            var teardown = File.ReadAllText(Path.Combine(repoRoot, "src", "Acta.SqlServer", "Sql", "Schema", "DropSchema.sql"))
                .Replace("{{schema}}", schema);
            await using var cleanupConn = new SqlConnection(connString);
            await cleanupConn.OpenAsync(CancellationToken.None);
            foreach (var batch in SplitOnGo(teardown))
            {
                await using var cmd = cleanupConn.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
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
