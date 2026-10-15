using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Acta;

/// <summary>
/// One discovered migration: its file stem, which its history row records, and its <c>{{schema}}</c>-templated body.
/// </summary>
internal sealed record SchemaMigration(string Name, string Template);

/// <summary>
/// Applies and verifies a provider assembly's <c>M{yyyyMMddHHmmss}_{name}.sql</c> migrations. Each file records its own
/// history row, so the runner only decides which files run; the dialect token selects the bootstrap operations.
/// </summary>
internal static partial class SchemaMigrations
{
    internal const int CommandTimeoutSeconds = 120;

    /// <summary>
    /// Applies every shipped migration with no history row, in stem order, in one transaction under the per-schema lock,
    /// so concurrent bootstrappers serialize.
    /// </summary>
    public static async Task ApplyAsync(
        DbConnection conn,
        string schemaName,
        string dialectToken,
        Assembly providerAssembly,
        CancellationToken ct
    )
    {
        var migrations = Discover(providerAssembly);
        var schema = Operations(conn, schemaName, dialectToken);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await schema.AcquireSchemaLockAsync(tx, schemaName, ct);
            await schema.EnsureMigrationsAsync(tx, ct);
            var applied = (await schema.LoadAppliedMigrationsAsync(tx, ct)).Names;

            foreach (var migration in migrations.Where(m => !applied.Contains(m.Name)))
            {
                foreach (var batch in SplitOnGo(migration.Template.Replace("{{schema}}", schemaName, StringComparison.Ordinal)))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// The read-only check every provider bootstrap runs: the history must hold a row for every migration this build ships.
    /// No lock, no transaction, no writes; the caller owns the open connection.
    /// </summary>
    public static async Task PreflightAsync(
        DbConnection conn,
        string schemaName,
        string dialectToken,
        Assembly providerAssembly,
        CancellationToken ct
    )
    {
        IReadOnlySet<string> applied;
        try
        {
            applied = (await Operations(conn, schemaName, dialectToken).LoadAppliedMigrationsAsync(transaction: null, ct)).Names;
        }
        catch (DbException ex)
        {
            // Whatever stopped the read - no table, no schema, no privilege - the operator's next step is the same:
            // provision, or point the host at the right schema and principal. The provider's message tells which.
            throw NotProvisioned(schemaName, dialectToken, ex);
        }

        Verify(Discover(providerAssembly), applied, dialectToken);
    }

    /// <summary>Drops the schema and re-applies every migration.</summary>
    public static async Task ResetSchemaAsync(
        DbConnection conn,
        string schemaName,
        string dialectToken,
        Assembly providerAssembly,
        CancellationToken ct
    )
    {
        await DropSchemaAsync(conn, schemaName, dialectToken, ct);
        await ApplyAsync(conn, schemaName, dialectToken, providerAssembly, ct);
    }

    /// <summary>
    /// Drops everything Acta installed in the schema and applies nothing after: the drop half of a reset, for a caller
    /// that wants the schema gone rather than fresh, such as a benchmark cell that has recorded its measurement.
    /// </summary>
    public static Task DropSchemaAsync(DbConnection conn, string schemaName, string dialectToken, CancellationToken ct) =>
        Operations(conn, schemaName, dialectToken).DropSchemaAsync(ct);

    /// <summary>
    /// Fails naming every shipped migration the history has no row for. Rows this build does not ship are tolerated: an
    /// older worker against a newer database is a supported deployment shape.
    /// </summary>
    internal static void Verify(IReadOnlyList<SchemaMigration> shipped, IReadOnlySet<string> applied, string dialectToken)
    {
        var missing = shipped.Where(m => !applied.Contains(m.Name)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // Named individually rather than as a count: the operator's next action is to run the script that supplies
        // exactly these, and the names are what make a partially applied provisioning run recognizable.
        throw new InvalidOperationException(
            $"This database is missing {(missing.Count == 1 ? "migration" : "migrations")} "
                + $"{string.Join(", ", missing.Select(m => m.Name))}, which this build of Acta ships and requires. Run the "
                + $"current provisioning script ({ProvisionScript(dialectToken)}) against the database before starting "
                + "workers; it applies exactly what is missing and skips what is present. Migrations are never applied at "
                + "startup while ApplyMigrationsOnStartup is false."
        );
    }

    /// <summary>
    /// The history the connected principal can read is not there: the schema was never provisioned, the host is pointed
    /// at the wrong database or schema name, or the principal cannot read it. <paramref name="cause"/> is the failed read.
    /// </summary>
    internal static InvalidOperationException NotProvisioned(string schemaName, string dialectToken, DbException? cause = null) =>
        new(
            $"The Acta schema is not provisioned: schema '{schemaName}' has no migrations table this principal can read. Run "
                + $"the current provisioning script ({ProvisionScript(dialectToken)}) against this database under a DDL-capable "
                + "principal, or set ApplyMigrationsOnStartup to let this host provision it (development only). If the schema "
                + "does exist, the host is pointed at the wrong database or the wrong schema name, or this principal cannot "
                + "read it."
                + (cause is null ? "" : $" The database said: {cause.Message}"),
            cause
        );

    /// <summary>
    /// The assembly's <c>Schema/Migrations</c> resources, in stem order: the fixed-width datetime prefix makes ordinal
    /// order the apply order.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> Discover(Assembly assembly)
    {
        var prefix = assembly.GetName().Name + ".Schema.Migrations.";
        var found = new List<SchemaMigration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            found.Add(new SchemaMigration(resource[prefix.Length..^".sql".Length], reader.ReadToEnd()));
        }

        return found.Count == 0
            ? throw new InvalidOperationException(
                $"No migration scripts found under Schema/Migrations in {assembly.GetName().Name}. "
                    + "The provider package must embed its complete Schema/Migrations/*.sql sequence."
            )
            : [.. found.OrderBy(m => m.Name, StringComparer.Ordinal)];
    }

    private static SchemaOperations Operations(DbConnection conn, string schemaName, string dialectToken) =>
        new(conn, new SqlResourceCatalog(dialectToken, schemaName), CommandTimeoutSeconds);

    /// <summary>The published per-dialect script.</summary>
    private static string ProvisionScript(string dialectToken) => $"docs/reference/schema-{dialectToken}.sql";

    /// <summary>
    /// The script's batches: split on lines holding only <c>GO</c>, trimmed, empty ones dropped. Every dialect's migrations
    /// pass through it; only SQL Server's carry <c>GO</c> lines. Don't put a bare <c>GO</c> line inside a string literal.
    /// </summary>
    internal static IEnumerable<string> SplitOnGo(string script) =>
        GoSeparatorRegex().Split(script).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparatorRegex();
}
