using Acta.Postgres;
using Acta.Tests.Conformance.Testing;
using Npgsql;
using Xunit;

namespace Acta.Tests.Conformance.Postgres;

/// <summary>
/// Explicit dev-environment reset entry point. Drops and re-applies M001 against one of the two
/// well-known schemas (<c>acta</c> for demos and manual dev, <c>acta_test</c> for the
/// append-only test schema). Skipped from normal runs via <see cref="FactAttribute.Explicit"/>.
/// </summary>
/// <remarks>
/// The M001 apply is delegated to <see cref="PostgresSchemaMigrator.ResetSchemaAsync"/>, the single
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
            IntegrationConfig.PostgresConnectionString
            ?? throw new InvalidOperationException("DatabaseSetup requires ACTA_TEST_PG to point at the dev Postgres.");

        var builder = new NpgsqlConnectionStringBuilder(conn) { Timeout = 30 };
        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(
                $"DatabaseSetup connection string must specify a Database. Got: {builder.ConnectionString}"
            );
        }

        EnsureDevDatabaseName(builder.Database);

        await using var c = new NpgsqlConnection(builder.ConnectionString);
        await c.OpenAsync(ct);
        await PostgresSchemaMigrator.ResetSchemaAsync(c, schemaName, ct);
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
