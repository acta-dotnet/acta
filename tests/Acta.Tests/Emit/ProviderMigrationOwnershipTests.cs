using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// Migrations are provider-owned: each provider package holds its own
/// <c>Migrations/M{yyyyMMddHHmmss}_{name}.sql</c> with bare, suffix-free names (the package is the
/// dialect), and core carries none. End-to-end proof
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

        // Allowed-locations rule: the only Migrations directories anywhere under src/ are the three
        // provider Migrations trees, so a stray folder in Acta, or a future
        // project fails here without needing its path enumerated.
        var strayMigrations = Directory
            .EnumerateDirectories(Path.Combine(root, "src"), "Migrations", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Contains("/bin/") && !relative.Contains("/obj/"))
            .Where(relative => !Providers.Any(p => relative == $"src/{p}/Migrations"))
            .ToArray();
        Assert.True(
            strayMigrations.Length == 0,
            "Migrations are provider-owned (src/{Provider}/Migrations only); stray: " + string.Join(", ", strayMigrations)
        );

        foreach (var provider in Providers)
        {
            var dir = Path.Combine(root, "src", provider, "Migrations");
            Assert.True(Directory.Exists(dir), $"Missing {provider}/Migrations.");

            var migrations = Directory.GetFiles(dir, "*.sql");
            Assert.NotEmpty(migrations);
            foreach (var file in migrations)
            {
                // Bare M{yyyyMMddHHmmss}_{name}.sql: no dialect suffix (the package is the dialect).
                Assert.Matches(@"^M[0-9]{14}_[a-z][a-z0-9_]*\.sql$", Path.GetFileName(file));
            }
        }
    }
}
