using System.Data.Common;
using System.Globalization;

namespace Acta;

/// <summary>
/// Looks for the migration-history table in the catalog, outside any transaction. The read-only preflight asks this
/// first, so an unprovisioned database is reported as unprovisioned rather than as the provider's "no such table" error.
/// </summary>
internal sealed class HasMigrationsTableHandler(DbConnection connection, SqlResourceCatalog sql, int commandTimeoutSeconds)
{
    public async Task<HasMigrationsTableResponse> HandleAsync(CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = commandTimeoutSeconds;
        cmd.CommandText = sql.Load("HasMigrationsTable");
        var found = await cmd.ExecuteScalarAsync(ct);
        return new HasMigrationsTableResponse(found is not null and not DBNull && Convert.ToInt64(found, CultureInfo.InvariantCulture) > 0);
    }
}
