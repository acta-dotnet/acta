using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Shared;

internal static class ProviderCatalog
{
    internal static IReadOnlyList<ProviderInfo> All { get; } =
    [
        new ProviderInfo("mssql", new SqlServerDdlDialect(), "Acta.SqlServer", "acta"),
        new ProviderInfo("pg", new PostgresDdlDialect(), "Acta.Postgres", "acta"),
        new ProviderInfo("sqlite", new SqliteDdlDialect(), "Acta.Sqlite", "main"),
    ];

    /// <summary>The provider for a dialect token, which every caller of a provider folder needs.</summary>
    internal static ProviderInfo ByToken(string token) =>
        All.SingleOrDefault(p => p.Token == token) ?? throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown dialect token.");
}

/// <summary>
/// One provider as the emitter sees it: its dialect token, its DDL dialect, its package folder under <c>src/</c>, and
/// the default schema its published script provisions.
/// </summary>
internal sealed record ProviderInfo(string Token, SqlDdlDialect Dialect, string Project, string Schema);
