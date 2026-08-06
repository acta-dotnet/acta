using System.Text.RegularExpressions;
using Acta.Tests.Conformance.SqlServer.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/provision/mssql.sql) must provision a working
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
        var published = File.ReadAllText(Path.Combine(repoRoot, "docs", "reference", "provision", "mssql.sql"));
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
            foreach (var batch in SplitOnGo(script))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT (SELECT COUNT(*) FROM {schema}.migrations WHERE version = 1), "
                + $"(SELECT COUNT(*) FROM {schema}.jobs_view), "
                + $"(SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('{schema}'))";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.Equal(1, reader.GetInt32(0));
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
}
