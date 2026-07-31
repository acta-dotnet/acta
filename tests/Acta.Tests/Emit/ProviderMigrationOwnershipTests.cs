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

        // Allowed-locations rule: the only Migrations directories anywhere under src/ are the three
        // provider Schema/Migrations trees, so a stray folder in Acta, Acta.Runtime, or a future
        // project fails here without needing its path enumerated.
        var strayMigrations = Directory
            .EnumerateDirectories(Path.Combine(root, "src"), "Migrations", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Contains("/bin/") && !relative.Contains("/obj/"))
            .Where(relative => !Providers.Any(p => relative == $"src/{p}/Schema/Migrations"))
            .ToArray();
        Assert.True(
            strayMigrations.Length == 0,
            "Migrations are provider-owned (src/{Provider}/Schema/Migrations only); stray: " + string.Join(", ", strayMigrations)
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
