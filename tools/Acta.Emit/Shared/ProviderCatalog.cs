using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Shared;

internal sealed record ProviderInfo(string Token, SqlDdlDialect Dialect, string Suffix);

internal static class ProviderCatalog
{
    internal static IReadOnlyList<ProviderInfo> All { get; } =
    [
        new ProviderInfo("mssql", new SqlServerDdlDialect(), "mssql"),
        new ProviderInfo("pg", new PostgresDdlDialect(), "pg"),
        new ProviderInfo("sqlite", new SqliteDdlDialect(), "sqlite"),
    ];

    internal static ProviderInfo? Resolve(string token) =>
        token.ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" => All[0],
            "pg" or "postgres" or "postgresql" => All[1],
            "sqlite" => All[2],
            _ => null,
        };
}
