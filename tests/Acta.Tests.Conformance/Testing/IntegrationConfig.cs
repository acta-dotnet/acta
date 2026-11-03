namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Reads integration-test connection strings from the environment and exposes the shared schema
/// name used by all conformance tests in this assembly.
/// </summary>
/// <remarks>
/// <c>ACTA_TEST_MSSQL</c> and <c>ACTA_TEST_PG</c> carry the SQL Server and Postgres connection
/// strings; the fixture targets the shared <see cref="TestSchemaName"/> schema inside each.
/// </remarks>
public static class IntegrationConfig
{
    /// <summary>
    /// Shared schema name used by every conformance test in this assembly. Underscore (not
    /// hyphen) so unquoted <c>{{schema}}.table</c> references in the migrations parse correctly on both
    /// providers.
    /// </summary>
    public const string TestSchemaName = "acta_test";

    /// <summary>
    /// SQL Server connection string, or <c>null</c> if the env var is unset. With <see cref="SqlServerRcsiOff"/> its
    /// catalog gains an <c>-rcsioff</c> suffix, so a run without read-committed snapshot never touches the main test
    /// database.
    /// </summary>
    public static string? SqlServerConnectionString =>
        Environment.GetEnvironmentVariable("ACTA_TEST_MSSQL") is { } connectionString && SqlServerRcsiOff
            ? WithCatalogSuffix(connectionString, "-rcsioff")
            : Environment.GetEnvironmentVariable("ACTA_TEST_MSSQL");

    /// <summary>
    /// <c>ACTA_TEST_MSSQL_RCSI=off</c>: the SQL Server bootstrap turns read-committed snapshot off after provisioning,
    /// so the suite runs on locking reads, as a database whose owner left the option off does.
    /// </summary>
    public static bool SqlServerRcsiOff =>
        string.Equals(Environment.GetEnvironmentVariable("ACTA_TEST_MSSQL_RCSI"), "off", StringComparison.OrdinalIgnoreCase);

    private static string WithCatalogSuffix(string connectionString, string suffix)
    {
        // Both aliases take the suffix: SqlClient honours whichever comes last, so either may name the catalog.
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "Initial Catalog", "Database" })
        {
            if (builder.TryGetValue(key, out var catalog))
            {
                builder[key] = $"{catalog}{suffix}";
            }
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// PostgreSQL connection string, or <c>null</c> if neither env var is set.
    /// </summary>
    public static string? PostgresConnectionString => Environment.GetEnvironmentVariable("ACTA_TEST_PG");

    /// <summary>
    /// Locate the repo root by walking upward from the test binary until <c>Acta.slnx</c> is
    /// found. Used to resolve the per-provider migration scripts.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"Could not locate Acta.slnx walking up from {AppContext.BaseDirectory}.");
    }
}
