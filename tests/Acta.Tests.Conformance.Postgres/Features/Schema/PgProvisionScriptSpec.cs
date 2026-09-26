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
            // Twice on purpose: the header promises that a re-run on a database the script provisioned
            // applies no migration twice, and the migration-row count below is what proves it.
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
            // script's own BEGIN banners, plus the version-0 baseline row and the object package row it records.
            var migrations = BeginBanner().Matches(published).Count + 2;
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
            Assert.Equal(59, reader.GetInt64(2));
        }
        finally
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE;";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Re-applying the pg provision script keeps data, grants, and dependent views")]
    public async Task Reapplied_script_keeps_data_grants_and_dependents()
    {
        var connString = IntegrationConfig.PostgresConnectionString;
        if (connString is null)
        {
            Assert.Skip("ACTA_TEST_PG is not set.");
        }
        var ct = TestContext.Current.CancellationToken;
        var schema = $"acta_provision_{Guid.NewGuid():N}"[..30];
        var role = $"acta_reader_{Guid.NewGuid():N}"[..30];
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

            // What a DBA adds around the published views: a grant to a reporting role and a view of
            // their own that selects from one of ours.
            await ExecuteAsync(
                conn,
                $"CREATE ROLE {role} NOLOGIN; GRANT SELECT ON {schema}.jobs_view TO {role}; "
                    + $"CREATE VIEW {schema}.dba_job_names AS SELECT job_ref, job_name, status FROM {schema}.jobs_view;",
                ct
            );

            // The script re-applied by hand, then the bootstrap's own installer: both rewrite the views.
            await ExecuteAsync(conn, script, ct);
            await AssertDbaObjectsSurviveAsync(job);
            await Acta.Postgres.Schema.PostgresSchemaMigrator.ApplyAsync(conn, schema, ct);
            await AssertDbaObjectsSurviveAsync(job);

            await ScriptProvisionedRuntime.AssertSucceededAsync(fixture.ApplyProvider, schema, job, 5, ct);
        }
        finally
        {
            // Its own connection, because a failed script leaves the test connection inside an aborted
            // transaction. The schema takes the dependent view and the grant with it, which frees the role.
            await using var cleanup = new NpgsqlConnection(connString);
            await cleanup.OpenAsync(CancellationToken.None);
            await ExecuteAsync(cleanup, $"DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE IF EXISTS {role};", CancellationToken.None);
        }

        async Task AssertDbaObjectsSurviveAsync(JobRef job)
        {
            await using var probe = conn.CreateCommand();
            probe.CommandText =
                $"SELECT has_table_privilege('{role}', '{schema}.jobs_view', 'SELECT'), "
                + $"(SELECT status FROM {schema}.dba_job_names WHERE job_ref = '{job.Value}')";
            await using var reader = await probe.ExecuteReaderAsync(ct);
            Assert.True(await reader.ReadAsync(ct));
            Assert.True(reader.GetBoolean(0), "the SELECT grant on jobs_view did not survive the re-apply");
            Assert.Equal("succeeded", reader.GetString(1));
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
