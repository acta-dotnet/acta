using System.Data.Common;

namespace Acta;

/// <summary><see cref="ISchemaOperations"/>, one line per operation, every room built over the caller's connection.</summary>
internal sealed class SchemaOperations(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds) : ISchemaOperations
{
    private readonly AcquireSchemaLockHandler _acquireSchemaLock = new(connection, sql, commandTimeoutSeconds);
    private readonly EnsureMigrationsHandler _ensureMigrations = new(connection, sql, commandTimeoutSeconds);
    private readonly LoadAppliedVersionsHandler _loadAppliedVersions = new(connection, sql, commandTimeoutSeconds);
    private readonly HasMigrationsTableHandler _hasMigrationsTable = new(connection, sql, commandTimeoutSeconds);
    private readonly DropSchemaHandler _dropSchema = new(connection, sql, commandTimeoutSeconds);

    public Task AcquireSchemaLockAsync(DbTransaction transaction, string schemaName, CancellationToken ct) =>
        _acquireSchemaLock.HandleAsync(new AcquireSchemaLockCommand(transaction, schemaName), ct);

    public Task EnsureMigrationsAsync(DbTransaction transaction, CancellationToken ct) =>
        _ensureMigrations.HandleAsync(new EnsureMigrationsCommand(transaction), ct);

    public Task<LoadAppliedVersionsResponse> LoadAppliedVersionsAsync(DbTransaction? transaction, CancellationToken ct) =>
        _loadAppliedVersions.HandleAsync(new LoadAppliedVersionsQuery(transaction), ct);

    public Task<HasMigrationsTableResponse> HasMigrationsTableAsync(CancellationToken ct) => _hasMigrationsTable.HandleAsync(ct);

    public Task DropSchemaAsync(CancellationToken ct) => _dropSchema.HandleAsync(ct);
}
