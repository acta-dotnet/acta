using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Shared;

/// <summary>
/// One provider as the emitter sees it: its dialect token, its DDL dialect, its package folder under <c>src/</c>, and
/// the default schema its published script provisions.
/// </summary>
internal sealed record ProviderInfo(string Token, SqlDdlDialect Dialect, string Project, string Schema);

internal static class ProviderCatalog
{
    internal static IReadOnlyList<ProviderInfo> All { get; } =
    [
        new ProviderInfo("mssql", new SqlServerDdlDialect(), "Acta.SqlServer", "acta"),
        new ProviderInfo("pg", new PostgresDdlDialect(), "Acta.Postgres", "acta"),
        new ProviderInfo("sqlite", new SqliteDdlDialect(), "Acta.Sqlite", "main"),
    ];

    internal static ProviderInfo? Resolve(string token) =>
        token.ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" => All[0],
            "pg" or "postgres" or "postgresql" => All[1],
            "sqlite" => All[2],
            _ => null,
        };

    /// <summary>The provider for a dialect token, which every caller of a provider folder needs.</summary>
    internal static ProviderInfo ByToken(string token) =>
        Resolve(token) ?? throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown dialect token.");
}
