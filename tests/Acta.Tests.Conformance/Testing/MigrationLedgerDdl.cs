using System.Reflection;
using Acta.Relational.Resources;
using Acta.Tests.Conformance.Sql;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// The provider's own <c>Sql/Schema/EnsureMigrations.sql</c>, rendered for a schema name. The
/// migration-history probes build their ledger from this rather than from a copy, so a probe cannot
/// disagree with the table the real migration runner creates and the preflight then reads.
/// </summary>
internal static class MigrationLedgerDdl
{
    /// <summary>
    /// Idempotent DDL creating the history ledger (and, on the schema-bearing providers, its schema)
    /// for <paramref name="schemaName"/> under the provider selected by <paramref name="dialectToken"/>.
    /// </summary>
    public static string For(string dialectToken, string schemaName) =>
        new SqlResourceCatalog(Assembly.Load(ProviderSqlResources.ProviderAssemblyName(dialectToken)), schemaName).Load(
            "Sql/Schema/EnsureMigrations.sql"
        );
}
