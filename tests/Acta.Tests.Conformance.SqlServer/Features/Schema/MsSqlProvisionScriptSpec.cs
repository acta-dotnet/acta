using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Sql;
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
/// instruction) in a database of the fact's own, which the fact drops when it ends.
/// </summary>
[Deploys("add-numbers")]
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
        var job = await ScriptProvisionedRuntime.RunJobAsync<MsSqlProvisionScriptSpec>(ApplyProvider, schema, 2, 3, ct);
        await RunScriptAsync(database.ConnectionString, script, ct);
        await ScriptProvisionedRuntime.AssertSucceededAsync<MsSqlProvisionScriptSpec>(ApplyProvider, schema, job, 5, ct);
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
            .Read("mssql", "CompleteJobStep")
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
            $"SELECT OBJECT_DEFINITION(OBJECT_ID('{schema}.complete_job_step')), (SELECT COUNT(*) FROM {schema}.migrations WHERE name = '{Newer}')";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Contains("newer_release_body", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact(
        DisplayName = "The published mssql script refuses a system database and changes nothing, even in a client that runs on past errors"
    )]
    public async Task Published_script_refuses_a_system_database_even_when_errors_are_ignored()
    {
        // tempdb rather than master: should the refusal ever stop working, what it lets through is gone at the next restart.
        var ct = TestContext.Current.CancellationToken;
        var tempdb = new SqlConnectionStringBuilder(ScratchDatabase.Master) { InitialCatalog = "tempdb" }.ConnectionString;
        await using var conn = new SqlConnection(tempdb);
        await conn.OpenAsync(ct);

        // Like sqlcmd without -b: every batch runs, whatever the one before it raised.
        var refused = false;
        foreach (var batch in SchemaMigrations.SplitOnGo(ReadPublishedScript()))
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = batch;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                refused |= ex.Message.Contains("never master, tempdb, model or msdb", StringComparison.Ordinal);
            }
        }

        Assert.True(refused, "the script did not refuse tempdb");
        Assert.IsType<DBNull>(await ScratchDatabase.ScalarAsync<object>(tempdb, "SELECT SCHEMA_ID(N'acta');", ct));
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
}
