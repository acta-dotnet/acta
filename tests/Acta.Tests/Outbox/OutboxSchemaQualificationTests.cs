using Acta.Postgres.Configuration;
using Acta.Relational.Resources;
using Acta.SqlServer.Configuration;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// Zero-configuration schema selection: with no schema override the source SQL must leave the outbox table
/// reference unqualified so the connection's database default resolves it (PostgreSQL search_path, SQL
/// Server login default), exactly as EF's null-schema mapping does; with an override the reference is
/// schema-qualified. A live default-schema drain would need a second login/search_path in the shared
/// single-namespace harness (disproportionate), so this proves the qualification at the SQL-generation
/// level: both the claim DML and the shape-introspection resolve the same way.
/// </summary>
public sealed class OutboxSchemaQualificationTests
{
    private static SqlResourceCatalog Pg(string? schema) => new(typeof(PostgresProviderOptions).Assembly, schema, "acta_outbox");

    private static SqlResourceCatalog Mssql(string? schema) => new(typeof(SqlServerProviderOptions).Assembly, schema, "acta_outbox");

    private static string Claim(SqlResourceCatalog catalog) => catalog.Load("Features/Outbox/Sql/ClaimDueRows.sql");

    [Fact]
    public void Postgres_leaves_the_table_unqualified_with_no_schema_override()
    {
        Assert.Contains("UPDATE acta_outbox", Claim(Pg(null)), StringComparison.Ordinal);
        Assert.DoesNotContain(".acta_outbox", Claim(Pg(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_qualifies_the_table_with_a_schema_override()
    {
        Assert.Contains("UPDATE app.acta_outbox", Claim(Pg("app")), StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_leaves_the_table_unqualified_with_no_schema_override()
    {
        Assert.Contains("UPDATE acta_outbox", Claim(Mssql(null)), StringComparison.Ordinal);
        Assert.DoesNotContain(".acta_outbox", Claim(Mssql(null)), StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_qualifies_the_table_with_a_schema_override()
    {
        Assert.Contains("UPDATE app.acta_outbox", Claim(Mssql("app")), StringComparison.Ordinal);
    }
}
