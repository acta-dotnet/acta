using System.Data.Common;

namespace Acta;

/// <summary>
/// Takes the per-schema installer lock inside the migration transaction and holds it until that transaction ends, so two
/// installers never interleave. SQLite names no lock: its immediate migration transaction already holds the one write lock.
/// </summary>
internal sealed class AcquireSchemaLockHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    /// <summary>
    /// The key every installer locks before it reads migration history: the bootstrap here, and the published provisioning
    /// scripts, which name the same key. The schema is its only word, so a script relocated by replacing the schema name
    /// throughout keeps naming the key its bootstrap takes.
    /// </summary>
    public static string LockKey(string schemaName) => $"{schemaName}-migrations";

    public async Task HandleAsync(AcquireSchemaLockCommand command, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = command.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("AcquireSchemaLock");
        var key = cmd.CreateParameter();
        key.ParameterName = "@p_key";
        key.Value = LockKey(command.SchemaName);
        cmd.Parameters.Add(key);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
