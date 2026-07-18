using Acta.Relational.Schema;
using Xunit;

namespace Acta.Tests.Migrations;

/// <summary>
/// Unit coverage for <see cref="SchemaMigrationDiscovery"/>. Discovery reads an assembly's embedded
/// <c>Schema/Migrations/M*.sql</c> for a given dialect and throws when none are present. End-to-end discovery
/// against the real M001 resources (now owned by the provider packages) is exercised by the provider
/// integration tests.
/// </summary>
public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void Discover_throws_when_the_assembly_embeds_no_migrations()
    {
        // Migrations live in provider packages; Acta.Relational itself embeds none.
        var relational = typeof(SchemaMigrationDiscovery).Assembly;
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaMigrationDiscovery.Discover(relational));
        Assert.Contains("No migration scripts found", ex.Message);
    }

    [Fact]
    public void MigrationScript_substitutes_schema()
    {
        var script = new SchemaMigration(
            Version: 1,
            Name: "M001_test",
            Template: "CREATE TABLE {{schema}}.foo (id int); -- second {{schema}} reference"
        );

        var result = script.SubstituteSchema("my_schema");

        Assert.Equal("CREATE TABLE my_schema.foo (id int); -- second my_schema reference", result);
    }
}
