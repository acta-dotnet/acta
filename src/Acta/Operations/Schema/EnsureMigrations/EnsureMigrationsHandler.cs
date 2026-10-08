using System.Data.Common;

namespace Acta;

/// <summary>
/// Creates the migration-history table, and on the servers its schema, when it is missing, inside the migration
/// transaction. An existing table keeps every row it has.
/// </summary>
internal sealed class EnsureMigrationsHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task HandleAsync(EnsureMigrationsCommand command, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = command.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("EnsureMigrations");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
