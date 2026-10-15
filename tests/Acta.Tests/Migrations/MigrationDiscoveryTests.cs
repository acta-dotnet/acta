using Xunit;

namespace Acta.Tests.Migrations;

/// <summary>
/// Unit coverage for <see cref="SchemaMigrations.Discover"/>. Discovery reads an assembly's embedded
/// <c>Schema/Migrations/*.sql</c> resources and throws when none are present. End-to-end
/// discovery against the real baselines (owned by the provider packages) is exercised by the provider integration tests.
/// </summary>
public sealed class MigrationDiscoveryTests
{
    [Fact]
    public void Discover_throws_when_the_assembly_embeds_no_migrations()
    {
        // Migrations live in provider packages; Acta itself embeds none.
        var relational = typeof(SchemaMigrations).Assembly;
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaMigrations.Discover(relational));
        Assert.Contains("No migration scripts found", ex.Message);
    }
}
