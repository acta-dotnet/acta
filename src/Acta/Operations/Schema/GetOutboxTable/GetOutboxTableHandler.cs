using System.Data.Common;
using System.Globalization;

namespace Acta;

/// <summary><see cref="SchemaOperations.GetOutboxTableAsync"/>: one probe of the catalog's outbox table.</summary>
internal sealed class GetOutboxTableHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task<GetOutboxTableResponse> HandleAsync(GetOutboxTableQuery query, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = query.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("GetOutboxTable");
        return new GetOutboxTableResponse(Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture));
    }
}
