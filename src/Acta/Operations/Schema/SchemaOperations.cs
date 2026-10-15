using System.Data.Common;

namespace Acta;

/// <summary>
/// The schema bootstrap's statements, each run over the caller's connection: the per-schema installer lock, the
/// migration-history table and its rows, and the drop.
/// </summary>
internal sealed class SchemaOperations(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    private readonly AcquireSchemaLockHandler _acquireSchemaLock = new(connection, sql, commandTimeoutSeconds);
    private readonly EnsureMigrationsHandler _ensureMigrations = new(connection, sql, commandTimeoutSeconds);
    private readonly LoadAppliedMigrationsHandler _loadAppliedMigrations = new(connection, sql, commandTimeoutSeconds);
    private readonly DropSchemaHandler _dropSchema = new(connection, sql, commandTimeoutSeconds);

    /// <summary>
    /// Takes the per-schema installer lock inside the migration transaction and holds it until that transaction ends, so two
    /// installers never interleave.
    /// </summary>
    public Task AcquireSchemaLockAsync(DbTransaction transaction, string schemaName, CancellationToken ct) =>
        _acquireSchemaLock.HandleAsync(new AcquireSchemaLockCommand(transaction, schemaName), ct);

    /// <summary>
    /// Creates the migration-history table when it is missing, inside the migration transaction. An existing table keeps
    /// every row it has.
    /// </summary>
    public Task EnsureMigrationsAsync(DbTransaction transaction, CancellationToken ct) =>
        _ensureMigrations.HandleAsync(new EnsureMigrationsCommand(transaction), ct);

    /// <summary>Reads the migration history, inside <paramref name="transaction"/> when one is given.</summary>
    public Task<LoadAppliedMigrationsResponse> LoadAppliedMigrationsAsync(DbTransaction? transaction, CancellationToken ct) =>
        _loadAppliedMigrations.HandleAsync(new LoadAppliedMigrationsQuery(transaction), ct);

    /// <summary>
    /// Drops everything Acta installed in the schema, the history table included, outside any transaction. A schema that is
    /// already gone drops nothing and succeeds.
    /// </summary>
    public Task DropSchemaAsync(CancellationToken ct) => _dropSchema.HandleAsync(ct);
}
