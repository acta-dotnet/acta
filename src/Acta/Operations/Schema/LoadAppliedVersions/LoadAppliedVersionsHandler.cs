using System.Data.Common;

namespace Acta;

/// <summary><see cref="SchemaOperations.LoadAppliedVersionsAsync"/>: one read of the history table.</summary>
internal sealed class LoadAppliedVersionsHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task<LoadAppliedVersionsResponse> HandleAsync(LoadAppliedVersionsQuery query, CancellationToken ct)
    {
        var versions = new Dictionary<int, string>();
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = query.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("LoadAppliedVersions");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetInt32(0), reader.GetString(1));
        }

        return new LoadAppliedVersionsResponse(versions);
    }
}
