using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Shared;

/// <summary>
/// One provider as the emitter sees it. <c>Project</c> is its package folder under <c>src/</c> and
/// <c>Schema</c> the default schema its published script provisions; both used to live in separate
/// switch tables, one per feature, and drifted per feature.
/// </summary>
internal sealed record ProviderInfo(string Token, SqlDdlDialect Dialect, string Suffix, string Project, string Schema);

internal static class ProviderCatalog
{
    internal static IReadOnlyList<ProviderInfo> All { get; } =
    [
        new ProviderInfo("mssql", new SqlServerDdlDialect(), "mssql", "Acta.SqlServer", "acta"),
        new ProviderInfo("pg", new PostgresDdlDialect(), "pg", "Acta.Postgres", "acta"),
        new ProviderInfo("sqlite", new SqliteDdlDialect(), "sqlite", "Acta.Sqlite", "main"),
    ];

    internal static ProviderInfo? Resolve(string token) =>
        token.ToLowerInvariant() switch
        {
            "mssql" or "sqlserver" => All[0],
            "pg" or "postgres" or "postgresql" => All[1],
            "sqlite" => All[2],
            _ => null,
        };

    /// <summary>The provider for a dialect suffix, which every caller of a provider folder needs.</summary>
    internal static ProviderInfo BySuffix(string suffix) =>
        Resolve(suffix) ?? throw new ArgumentOutOfRangeException(nameof(suffix), suffix, "Unknown dialect suffix.");
}
