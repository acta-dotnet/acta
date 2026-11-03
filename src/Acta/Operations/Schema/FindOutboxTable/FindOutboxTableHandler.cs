using System.Data.Common;
using System.Globalization;

namespace Acta;

/// <summary><see cref="SchemaOperations.FindOutboxTableAsync"/>: one probe of the catalog's outbox table.</summary>
internal sealed class FindOutboxTableHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task<FindOutboxTableResponse> HandleAsync(FindOutboxTableQuery query, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = query.Transaction;
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("FindOutboxTable");
        return new FindOutboxTableResponse(Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture));
    }
}
