using System.Data.Common;
using System.Globalization;

namespace Acta;

/// <summary><see cref="ISchemaOperations.HasMigrationsTableAsync"/>: counts the table in the provider's catalog.</summary>
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
