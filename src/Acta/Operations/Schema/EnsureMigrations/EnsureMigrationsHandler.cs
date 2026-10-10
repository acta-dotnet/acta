using System.Data.Common;

namespace Acta;

/// <summary><see cref="SchemaOperations.EnsureMigrationsAsync"/>. On the servers it creates the schema too.</summary>
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
