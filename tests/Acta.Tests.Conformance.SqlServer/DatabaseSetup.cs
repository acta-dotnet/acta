using Acta.SqlServer;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Acta.Tests.Conformance.SqlServer;

/// <summary>
/// Explicit dev-environment reset entry point. Drops and re-applies M001 against one of the two
/// well-known schemas (<c>acta</c> for demos and manual dev, <c>acta_test</c> for the
/// append-only test schema). Skipped from normal runs via <see cref="FactAttribute.Explicit"/>.
/// </summary>
/// <remarks>
/// The M001 apply is delegated to <see cref="SqlServerSchemaMigrator.ResetSchemaAsync"/>, the single
/// source of truth for migration mechanics.
/// </remarks>
public sealed class DatabaseSetup
{
    private const string DevSchema = "acta";
    private const string TestSchema = "acta_test";

    [Fact(Explicit = true)]
    public Task ResetActaSchema() => ResetSchemaAsync(DevSchema, TestContext.Current.CancellationToken);

    /// <summary>
    /// Resets the append-only test schema used by the <c>Testing/</c> bases.
    /// Run when the accumulated row count gets unwieldy or after a destructive schema change.
    /// </summary>
    [Fact(Explicit = true)]
    public Task ResetActaTestSchema() => ResetSchemaAsync(TestSchema, TestContext.Current.CancellationToken);

    private static async Task ResetSchemaAsync(string schemaName, CancellationToken ct)
    {
        var conn =
            IntegrationConfig.SqlServerConnectionString
            ?? throw new InvalidOperationException("DatabaseSetup requires ACTA_TEST_MSSQL to point at the dev SQL Server.");

        var builder = new SqlConnectionStringBuilder(conn) { TrustServerCertificate = true, ConnectTimeout = 30 };
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException(
                $"DatabaseSetup connection string must specify an Initial Catalog (target database). Got: {builder.ConnectionString}"
            );
        }

        EnsureDevDatabaseName(builder.InitialCatalog);

        await using var c = new SqlConnection(builder.ConnectionString);
        await c.OpenAsync(ct);
        await SqlServerSchemaMigrator.ResetSchemaAsync(c, schemaName, ct);
    }

    private static void EnsureDevDatabaseName(string databaseName)
    {
        // Whitelist of known dev / test database names. Anyone introducing a new dev DB adds it
        // here explicitly; a substring match on "acta" would also accept "acta-prod".
        var allowed = new[] { "acta-test" };
        var isAllowed =
            allowed.Any(a => string.Equals(databaseName, a, StringComparison.OrdinalIgnoreCase))
            || databaseName.Contains("test", StringComparison.OrdinalIgnoreCase);

        if (!isAllowed)
        {
            throw new InvalidOperationException(
                $"Refusing to reset schema in database '{databaseName}': name must be one of "
                    + $"[{string.Join(", ", allowed)}] or contain 'test'. Extend the whitelist if you "
                    + "truly intend to allow another database."
            );
        }
    }
}
