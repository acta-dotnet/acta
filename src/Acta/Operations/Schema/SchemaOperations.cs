using System.Data.Common;

namespace Acta;

/// <summary>
/// The schema bootstrap's statements, each run over the caller's connection: the installer lock, the migration-history
/// table and its rows, the drop, and the probe of a producer's outbox table.
/// </summary>
internal sealed class SchemaOperations(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    private readonly AcquireSchemaLockHandler _acquireSchemaLock = new(connection, sql, commandTimeoutSeconds);
    private readonly EnsureMigrationsHandler _ensureMigrations = new(connection, sql, commandTimeoutSeconds);
    private readonly LoadAppliedMigrationsHandler _loadAppliedMigrations = new(connection, sql, commandTimeoutSeconds);
    private readonly DropSchemaHandler _dropSchema = new(connection, sql, commandTimeoutSeconds);
    private readonly FindOutboxTableHandler _findOutboxTable = new(connection, sql, commandTimeoutSeconds);

    /// <summary>
    /// Takes the per-schema installer lock inside the migration transaction and holds it until that transaction ends, so two
    /// installers never interleave.
    /// </summary>
    public Task AcquireSchemaLockAsync(DbTransaction transaction, string schemaName, CancellationToken ct) =>
        _acquireSchemaLock.HandleAsync(new AcquireSchemaLockCommand(transaction, AcquireSchemaLockHandler.LockKey(schemaName)), ct);

    /// <summary>The same lock under the database's outbox key, inside the outbox install transaction.</summary>
    public Task AcquireOutboxLockAsync(DbTransaction transaction, CancellationToken ct) =>
        _acquireSchemaLock.HandleAsync(new AcquireSchemaLockCommand(transaction, AcquireSchemaLockHandler.OutboxLockKey), ct);

    /// <summary>Whether the catalog's outbox table exists, read inside the outbox install transaction.</summary>
    public Task<FindOutboxTableResponse> FindOutboxTableAsync(DbTransaction transaction, CancellationToken ct) =>
        _findOutboxTable.HandleAsync(new FindOutboxTableQuery(transaction), ct);

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
