using System.Data.Common;

namespace Acta;

/// <summary>
/// Drops everything Acta installed in the schema, the history table included, outside any transaction. The servers drop
/// the schema itself; SQLite's schema is the database the application may share, so it drops Acta's views and tables by
/// name and leaves the rest. A schema that is already gone drops nothing and succeeds.
/// </summary>
internal sealed class DropSchemaHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task HandleAsync(CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("DropSchema");
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
