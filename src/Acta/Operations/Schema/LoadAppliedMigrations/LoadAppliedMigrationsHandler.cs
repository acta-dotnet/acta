using System.Data.Common;

namespace Acta;

/// <summary><see cref="SchemaOperations.LoadAppliedMigrationsAsync"/>: one read of the history table.</summary>
internal sealed class LoadAppliedMigrationsHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task<LoadAppliedMigrationsResponse> HandleAsync(LoadAppliedMigrationsQuery query, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = query.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("LoadAppliedMigrations");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return new LoadAppliedMigrationsResponse(names);
    }
}
