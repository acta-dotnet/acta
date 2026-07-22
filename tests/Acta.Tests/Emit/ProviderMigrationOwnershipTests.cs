using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Migrations are provider-owned: each provider package holds its own <c>Schema/Migrations/M{nnn}_*.sql</c> with
/// bare, suffix-free names (the package is the dialect), and core no longer carries any. End-to-end proof
/// that a provider applies its embedded migration is the provider conformance suite; this guards the
/// source-tree ownership and the no-foreign-dialect-suffix invariant.
/// </summary>
public sealed class ProviderMigrationOwnershipTests
{
    private static readonly string[] Providers = ["Acta.Sqlite", "Acta.Postgres", "Acta.SqlServer"];

    [Fact]
    public void Each_provider_owns_bare_migrations_and_core_has_none()
    {
        var root = IntegrationConfig.FindRepoRoot();

        Assert.False(
            Directory.Exists(Path.Combine(root, "src", "Acta", "Migrations")),
            "Core src/Acta/Migrations must not exist; migrations are provider-owned."
        );

        foreach (var provider in Providers)
        {
            var dir = Path.Combine(root, "src", provider, "Schema", "Migrations");
            Assert.True(Directory.Exists(dir), $"Missing {provider}/Schema/Migrations.");

            var migrations = Directory.GetFiles(dir, "M*.sql");
            Assert.NotEmpty(migrations);
            foreach (var file in migrations)
            {
                // Bare M{nnn}_{name}.sql: no dialect suffix (the package is the dialect).
                Assert.Matches(@"^M[0-9]{3}_[a-z0-9_]+\.sql$", Path.GetFileName(file));
            }
        }
    }
}
