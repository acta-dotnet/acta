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
    // M001 was destructively re-cut during preview for the frozen byte-sized persisted-code
    // contract. Earlier preview databases stamped M001 as "init" and cannot be translated safely.
    private const string RequiredPreviewBaselineName = "init-byte-codes-v1";

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

            if (
                applied.TryGetValue(1, out var baselineName)
                && !string.Equals(baselineName, RequiredPreviewBaselineName, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"The installed Acta preview schema uses incompatible M001 baseline '{baselineName}'. "
                        + "Drop and reprovision the database before upgrading to the byte-sized persisted-code contract."
                );
            }

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

    public static async Task ResetSchemaAsync(
        DbConnection conn,
        string schemaName,
        SchemaMigrationProviderHooks hooks,
        CancellationToken ct
    )
    {
        var sql = new SqlResourceCatalog(hooks.ProviderAssembly, schemaName);
        await SchemaCommands.DropSchema(conn, hooks, sql, ct);
        await ApplyAsync(conn, schemaName, hooks, ct);
    }
}

/// <summary>
/// Provider-specific hooks used by the shared migration runner. The provider assembly is the single
/// owner of migrations, schema commands, routines, and views for that provider.
/// </summary>
internal sealed record SchemaMigrationProviderHooks(
    Assembly ProviderAssembly,
    string DialectToken,
    Func<string, IEnumerable<string>> SplitBatches,
    string? PreludeSql = null,
    int CommandTimeoutSeconds = 120,
    string? ObjectDefinitionSql = null
);
