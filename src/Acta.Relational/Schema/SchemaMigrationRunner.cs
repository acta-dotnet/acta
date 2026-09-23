using System.Data.Common;
using System.Reflection;
using Acta.Relational.Resources;

namespace Acta.Relational.Schema;

/// <summary>
/// Provider-neutral apply/reset orchestration for provider-owned <c>Mnnn_*.sql</c> migrations.
/// Provider hooks supply the assembly, batch splitting, and connection-level behavior; every
/// executable schema command and SQL object body is loaded from that provider assembly.
/// </summary>
internal static class SchemaMigrationRunner
{
    /// <summary>
    /// Applies pending migrations in one transaction: take the per-schema lock, ensure the
    /// migrations table, read applied versions, run every missing script, then install current
    /// operator views and routines. Concurrent bootstrappers serialize on the lock.
    /// </summary>
    public static async Task ApplyAsync(DbConnection conn, string schemaName, SchemaMigrationProviderHooks hooks, CancellationToken ct)
    {
        if (hooks.PreludeSql is { } preludeSql)
        {
            await using var prelude = conn.CreateCommand();
            prelude.CommandText = preludeSql;
            prelude.CommandTimeout = hooks.CommandTimeoutSeconds;
            await prelude.ExecuteNonQueryAsync(ct);
        }

        var migrations = SchemaMigrationDiscovery.Discover(hooks.ProviderAssembly);
        var sql = new SqlResourceCatalog(hooks.ProviderAssembly, schemaName);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await SchemaCommands.AcquireLock(conn, tx, schemaName, hooks, sql, ct);
            await SchemaCommands.EnsureMigrations(conn, tx, hooks, sql, ct);
            var applied = await SchemaCommands.LoadAppliedVersions(conn, tx, hooks, sql, ct);

            VerifyBaselineStamp(applied, hooks.RequiredBaselineStamp);
            VerifyAppliedNames(migrations, applied);

            foreach (var migration in migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                foreach (var batch in hooks.SplitBatches(migration.SubstituteSchema(schemaName)))
                {
                    var trimmed = batch.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = trimmed;
                    cmd.CommandTimeout = hooks.CommandTimeoutSeconds;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await SqlObjectInstaller.Run(conn, tx, schemaName, hooks, sql, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// The version-0 sentinel row carries the baseline stamp; a non-empty history without it (or with
    /// a different stamp) was built by another baseline generation, including the pre-sentinel
    /// bookkeeping shape, and cannot be translated onto this build's schema: every baseline statement
    /// is existence-guarded, so such a database would take this build's baseline as a no-op and keep
    /// running the shape it already has. Refusing here is what turns that silence into a verdict.
    /// Shared with <see cref="MigrationHistoryPreflight"/> so the apply path and the always-runs
    /// read-only preflight cannot drift into two different verdicts on the same history.
    /// </summary>
    internal static void VerifyBaselineStamp(IReadOnlyDictionary<int, string> applied, string requiredStamp)
    {
        if (applied.Count == 0 || string.Equals(applied.GetValueOrDefault(0), requiredStamp, StringComparison.Ordinal))
        {
            return;
        }

        var recorded = applied.GetValueOrDefault(0) ?? applied.GetValueOrDefault(1) ?? "unknown";
        throw new InvalidOperationException(
            $"This database was built from Acta baseline '{recorded}', but this build ships baseline "
                + $"'{requiredStamp}'. One baseline carries no translation path onto another, so drop and "
                + "reprovision the database to move to this build."
        );
    }

    /// <summary>
    /// Every migration persists its bare snake name (the baseline stamp lives in the version-0
    /// sentinel row, checked separately); a mismatch means the on-disk migration was re-cut after
    /// this database already applied it - skipping silently would hide real drift.
    /// </summary>
    internal static void VerifyAppliedNames(IReadOnlyList<SchemaMigration> migrations, IReadOnlyDictionary<int, string> applied)
    {
        foreach (var migration in migrations)
        {
            var shipped = migration.Name[5..]; // bare snake name behind the "Mnnn_" prefix
            if (
                applied.TryGetValue(migration.Version, out var appliedName)
                && !string.Equals(appliedName, shipped, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"This database recorded migration {migration.Version} as '{appliedName}', but this build ships it "
                        + $"as '{shipped}'. A migration can only be amended before any database has applied it, so drop "
                        + "and reprovision the database or restore the original migration."
                );
            }
        }
    }

    public static async Task ResetSchemaAsync(
        DbConnection conn,
        string schemaName,
        SchemaMigrationProviderHooks hooks,
        CancellationToken ct
    )
    {
        await DropSchemaAsync(conn, schemaName, hooks, ct);
        await ApplyAsync(conn, schemaName, hooks, ct);
    }

    /// <summary>
    /// Drops everything the provider's schema drop script covers, the schema included, and applies
    /// nothing after. The drop half of a reset, exposed for callers that want a schema gone rather than
    /// fresh, such as a benchmark cell that has recorded its measurement.
    /// </summary>
    public static Task DropSchemaAsync(DbConnection conn, string schemaName, SchemaMigrationProviderHooks hooks, CancellationToken ct) =>
        SchemaCommands.DropSchema(conn, hooks, new SqlResourceCatalog(hooks.ProviderAssembly, schemaName), ct);
}

/// <summary>
/// Provider-specific hooks used by the shared migration runner. The provider assembly is the single
/// owner of migrations, schema commands, routines, and views for that provider.
/// </summary>
internal sealed record SchemaMigrationProviderHooks(
    Assembly ProviderAssembly,
    string DialectToken,
    Func<string, IEnumerable<string>> SplitBatches,
    // The generation of the baseline migration this provider embeds, from BaselineStamps: the value
    // the emitter wrote into that migration's version-0 row, and so the only history this package can
    // run against.
    string RequiredBaselineStamp,
    string? PreludeSql = null,
    int CommandTimeoutSeconds = 120,
    string? ObjectDefinitionSql = null
);
