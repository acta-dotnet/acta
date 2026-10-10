using System.Data.Common;

namespace Acta;

/// <summary>
/// The schema bootstrap's statements, run on the connection the door is built over: the per-schema installer lock, the
/// migration-history table and its rows, and the drop.
/// </summary>
internal interface ISchemaOperations
{
    /// <summary>
    /// Takes the per-schema installer lock inside the migration transaction and holds it until that transaction ends, so two
    /// installers never interleave.
    /// </summary>
    Task AcquireSchemaLockAsync(DbTransaction transaction, string schemaName, CancellationToken ct);

    /// <summary>
    /// Creates the migration-history table when it is missing, inside the migration transaction. An existing table keeps
    /// every row it has.
    /// </summary>
    Task EnsureMigrationsAsync(DbTransaction transaction, CancellationToken ct);

    /// <summary>Reads the migration history, inside <paramref name="transaction"/> when one is given.</summary>
    Task<LoadAppliedVersionsResponse> LoadAppliedVersionsAsync(DbTransaction? transaction, CancellationToken ct);

    /// <summary>
    /// Whether the migration-history table exists, read outside any transaction. The read-only preflight asks this first,
    /// so an unprovisioned database is reported as unprovisioned rather than as the provider's "no such table" error.
    /// </summary>
    Task<HasMigrationsTableResponse> HasMigrationsTableAsync(CancellationToken ct);

    /// <summary>
    /// Drops everything Acta installed in the schema, the history table included, outside any transaction. A schema that is
    /// already gone drops nothing and succeeds.
    /// </summary>
    Task DropSchemaAsync(CancellationToken ct);
}
