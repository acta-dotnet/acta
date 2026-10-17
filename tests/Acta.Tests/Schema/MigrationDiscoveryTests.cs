using Xunit;

namespace Acta.Tests.Schema;

/// <summary>
/// Unit coverage for <see cref="SchemaMigrations.Discover"/>. Discovery reads an assembly's embedded
/// <c>Migrations/*.sql</c> resources and throws when none are present or two share a name. End-to-end
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

    [Fact]
    public void Two_migrations_sharing_a_name_are_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SchemaMigrations.InApplyOrder([
                new("M20260102000000_add_x", ""),
                new("M20260101000000_init", ""),
                new("M20260103000000_add_x", ""),
            ])
        );
        Assert.Contains("M20260102000000_add_x and M20260103000000_add_x share the name 'add_x'", ex.Message);
    }

    [Fact]
    public void Distinct_names_come_back_in_stem_order()
    {
        var ordered = SchemaMigrations.InApplyOrder([new("M20260102000000_add_x", ""), new("M20260101000000_init", "")]);
        Assert.Equal(["M20260101000000_init", "M20260102000000_add_x"], ordered.Select(m => m.Name));
    }
}
