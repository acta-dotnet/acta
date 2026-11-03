using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer.Features.Schema;

/// <summary>
/// The published provisioning script (docs/reference/schema-mssql.sql) must provision a working
/// schema when run verbatim (batch by GO batch, as sqlcmd/SSMS would), because DBA-run provisioning
/// under an elevated principal is exactly how locked-down deployments consume it. Runs the committed
/// file against a fresh schema name (using the header's own "replace the schema name throughout"
/// instruction) in a database of the fact's own: the script's first batch turns read-committed snapshot
/// on, which disconnects every other session in its database, so it never runs in the shared one.
/// </summary>
public sealed partial class MsSqlProvisionScriptSpec
{
    private const string Newer = "M99991231235959_newer";

    [Fact(DisplayName = "The published mssql provision script provisions a fresh schema verbatim")]
    public async Task Published_script_provisions_a_fresh_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var published = ReadPublishedScript();
        var script = SchemaWord().Replace(published, schema);
        await using var database = await ScratchDatabase.CreateAsync(ct);

        // Twice on purpose: the header promises that a re-run on a database the script provisioned
        // applies no migration twice, and the migration-row count below is what proves it.
        await RunScriptAsync(database.ConnectionString, script, ct);
        await RunScriptAsync(database.ConnectionString, script, ct);

        // One history row per migration section in the file, no more (the double run must not
        // stamp anything twice), counted from the script's own BEGIN banners.
        var migrations = ProvisionScriptBanners.MigrationCount(published);
        Assert.True(migrations > 0, "the published script contains no migration banners");

        await using var conn = new SqlConnection(database.ConnectionString);
        await conn.OpenAsync(ct);
        await using var probe = conn.CreateCommand();
        probe.CommandText =
            $"SELECT (SELECT COUNT(*) FROM {schema}.migrations), "
            + $"(SELECT COUNT(*) FROM {schema}.jobs), "
            + $"(SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('{schema}'))";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(migrations, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(14, reader.GetInt32(2));
    }

    [Fact(DisplayName = "A mssql schema provisioned by the script runs with migrations disabled and keeps its data across a re-run")]
    public async Task Script_provisioned_schema_runs_and_keeps_its_data_across_a_rerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var script = SchemaWord().Replace(ReadPublishedScript(), schema);
        await using var database = await ScratchDatabase.CreateAsync(ct);
        void ApplyProvider(IActaBuilder builder, string schemaName) =>
            builder.UseSqlServer(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.Schema = schemaName;
                o.DeadlockRetryAttempts = 1;
            });

        await RunScriptAsync(database.ConnectionString, script, ct);
        var job = await ScriptProvisionedRuntime.RunJobAsync(ApplyProvider, schema, 2, 3, ct);
        await RunScriptAsync(database.ConnectionString, script, ct);
        await ScriptProvisionedRuntime.AssertSucceededAsync(ApplyProvider, schema, job, 5, ct);
    }

    [Fact(DisplayName = "An older script over a newer release's migration changes nothing, even in a client that runs on past errors")]
    public async Task An_older_script_over_a_newer_migration_changes_nothing_when_errors_are_ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_noexec_{Guid.NewGuid():N}"[..30];
        var script = SchemaWord().Replace(ReadPublishedScript(), schema);
        await using var database = await ScratchDatabase.CreateAsync(ct);
        await RunScriptAsync(database.ConnectionString, script, ct);

        // A newer release recorded a migration this script lacks, and one of its routines reads differently.
        await using var conn = new SqlConnection(database.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var newer = conn.CreateCommand())
        {
            newer.CommandText = $"INSERT INTO {schema}.migrations (name) VALUES ('{Newer}')";
            await newer.ExecuteNonQueryAsync(ct);
        }
        var marked = ProviderSqlResources
            .Read("mssql", "CompleteStep")
            .ReplaceLineEndings("\n")
            .Replace("{{schema}}", schema)
            .Replace("AS\nBEGIN", "AS\nBEGIN\n    -- newer_release_body");
        foreach (var batch in SchemaMigrations.SplitOnGo(marked))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = batch;
            await alter.ExecuteNonQueryAsync(ct);
        }

        // Like sqlcmd without -b: every batch runs, whatever the one before it raised.
        var refused = false;
        foreach (var batch in SchemaMigrations.SplitOnGo(script))
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

        Assert.True(refused, "the script did not refuse the newer migration");
        await using var probe = conn.CreateCommand();
        probe.CommandText =
            $"SELECT OBJECT_DEFINITION(OBJECT_ID('{schema}.complete_step')), (SELECT COUNT(*) FROM {schema}.migrations WHERE name = '{Newer}')";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Contains("newer_release_body", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    private static string ReadPublishedScript() =>
        File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-mssql.sql"));

    private static async Task RunScriptAsync(string connString, string script, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync(ct);
        foreach (var batch in SchemaMigrations.SplitOnGo(script))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // The whole-word lowercase schema name, exactly what the script header tells a DBA to replace.
    [GeneratedRegex(@"\bacta\b")]
    private static partial Regex SchemaWord();

    /// <summary>A database the fact creates through <c>master</c> and drops when it ends, its sessions included.</summary>
    private sealed class ScratchDatabase(string master, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;

        public static async Task<ScratchDatabase> CreateAsync(CancellationToken ct)
        {
            var shared = IntegrationConfig.SqlServerConnectionString;
            Assert.SkipWhen(shared is null, "ACTA_TEST_MSSQL is not set.");
            var builder = new SqlConnectionStringBuilder(shared) { TrustServerCertificate = true };
            var name = $"acta-test-provision-{Guid.NewGuid():N}"[..36];
            var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            await ExecuteAsync(master, $"CREATE DATABASE [{name}];", ct);
            return new ScratchDatabase(
                master,
                name,
                new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = name }.ConnectionString
            );
        }

        public async ValueTask DisposeAsync() =>
            await ExecuteAsync(
                master,
                $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];",
                CancellationToken.None
            );

        private static async Task ExecuteAsync(string connString, string sql, CancellationToken ct)
        {
            await using var conn = new SqlConnection(connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
