using System.Data;
using System.Data.Common;
using Acta.Relational.Resources;

namespace Acta.Relational.Schema;

/// <summary>
/// The read-only check every provider bootstrap runs, whether or not it applies migrations: the
/// database's migration history must carry this build's baseline stamp and already contain every
/// migration this build ships. No lock, no transaction, no writes.
/// </summary>
internal static class MigrationHistoryPreflight
{
    // A host with ApplyMigrationsOnStartup = false - what production is told to run - reached the
    // stamp and name checks only through the apply path it never takes, so the failure those checks
    // exist for (a worker pointed at a database from another baseline generation) surfaced at the
    // first real query instead of at startup. This makes the same comparisons unconditionally.
    //
    // History rows this build knows nothing about are accepted on purpose: an older worker against a
    // newer database is a supported, tested deployment shape, so only missing versions are a failure.
    //
    // History, not schema verification. Operator views and stored routines carry no version and are
    // rewritten at every bootstrap that applies migrations, so a database can hold correct history
    // alongside executable SQL from an older build - which is why an upgrade must run the current
    // provisioning script rather than trust a green preflight. An object-set fingerprint would close
    // that gap if operational evidence ever asks for one.

    /// <summary>
    /// Verifies <paramref name="schemaName"/>'s migration history against the migrations
    /// <paramref name="hooks"/>' provider assembly ships. Opens nothing and writes nothing; the
    /// caller owns the connection.
    /// </summary>
    public static async Task RunAsync(DbConnection conn, string schemaName, SchemaMigrationProviderHooks hooks, CancellationToken ct)
    {
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        var sql = new SqlResourceCatalog(hooks.ProviderAssembly, schemaName);
        if (!await SchemaCommands.MigrationsTableExists(conn, hooks, sql, ct))
        {
            throw NotProvisioned(schemaName, hooks.DialectToken);
        }

        Verify(
            SchemaMigrationDiscovery.Discover(hooks.ProviderAssembly),
            await SchemaCommands.LoadAppliedVersions(conn, tx: null, hooks, sql, ct),
            hooks.DialectToken
        );
    }

    /// <summary>
    /// The three verdicts, in the order an operator can act on them: wrong baseline generation, a
    /// migration re-cut after this database applied it, then a migration this build ships that the
    /// database has never applied.
    /// </summary>
    internal static void Verify(IReadOnlyList<SchemaMigration> migrations, IReadOnlyDictionary<int, string> applied, string dialectToken)
    {
        SchemaMigrationRunner.VerifyBaselineStamp(applied);
        SchemaMigrationRunner.VerifyAppliedNames(migrations, applied);

        var missing = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // Named individually rather than as a count: the operator's next action is to run the script
        // that supplies exactly these, and "M004 is missing" is what makes a partially applied
        // provisioning run recognizable.
        throw new InvalidOperationException(
            $"This database is missing {(missing.Count == 1 ? "migration" : "migrations")} "
                + $"{string.Join(", ", missing.Select(m => m.Name))}, which this build of Acta ships and requires. Run the "
                + $"current provisioning script ({ProvisionScript(dialectToken)}) against the database before starting "
                + "workers; it applies exactly what is missing and skips what is present. Migrations are never applied at "
                + "startup while ApplyMigrationsOnStartup is false."
        );
    }

    /// <summary>
    /// No migration-history ledger the connected principal can see: the schema was never provisioned,
    /// the host is pointed at the wrong database or schema name, or the principal cannot read the table.
    /// </summary>
    internal static InvalidOperationException NotProvisioned(string schemaName, string dialectToken) =>
        new(
            $"The Acta schema is not provisioned: schema '{schemaName}' has no migrations table. Run the current "
                + $"provisioning script ({ProvisionScript(dialectToken)}) against this database under a DDL-capable "
                + "principal, or set ApplyMigrationsOnStartup to let this host provision it (development only). If the "
                + "schema does exist, the host is pointed at the wrong database or the wrong schema name, or this "
                + "principal cannot read it: the catalog this check reads is permission-filtered, so a table the "
                + "principal has no privilege on is indistinguishable from a table that is not there."
        );

    // The published per-dialect script, named by the same token the provider hooks carry.
    private static string ProvisionScript(string dialectToken) => $"docs/reference/schema-{dialectToken}.sql";
}
