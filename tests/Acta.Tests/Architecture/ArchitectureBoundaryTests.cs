using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>Locks the dependency and source-layout boundaries in CONTRIBUTING.md, "Source layout".</summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] ProviderProjects = ["Acta.Postgres", "Acta.SqlServer", "Acta.Sqlite"];

    /// <summary>
    /// Concrete schema migrators are implementation mechanics, not a consumer contract: provider
    /// installation goes through the builder extensions (with ApplyMigrationsOnStartup), so the
    /// migrator classes stay internal.
    /// </summary>
    [Fact]
    public void Provider_schema_migrators_are_internal()
    {
        string[] migrators =
        [
            "Acta.Postgres.PostgresSchemaMigrator, Acta.Postgres",
            "Acta.SqlServer.SqlServerSchemaMigrator, Acta.SqlServer",
            "Acta.Sqlite.SqliteSchemaMigrator, Acta.Sqlite",
        ];
        foreach (var name in migrators)
        {
            var type = Type.GetType(name, throwOnError: true)!;
            Assert.False(type.IsPublic, $"{type.FullName} must stay internal.");
        }
    }

    /// <summary>
    /// The operations adapter consumes the public Acta API only: provider and schema capabilities
    /// arrive through SDK types (SqlProviderOptions), never through a provider, and Acta grants it no
    /// internals.
    /// </summary>
    [Fact]
    public void AspNetCore_references_only_the_public_api()
    {
        var references = typeof(ActaEndpointRouteBuilderExtensions)
            .Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.Contains("Acta", references);
        Assert.DoesNotContain(references, name => name!.StartsWith("Acta.", StringComparison.Ordinal));
    }

    /// <summary>
    /// EF Core is gone from the repository. With the former EF-based producer outbox package removed the
    /// producer story is the provider-package staging primitives, so no project anywhere (src, tests, tools)
    /// may reference an EF Core package: this walks every project's direct package references and its full
    /// ProjectReference closure and fails on any transitive EF package.
    /// </summary>
    [Fact]
    public void EntityFrameworkCore_stays_out_of_every_repository_project()
    {
        var repoRoot = ResolveRepoRoot();
        var failures = new List<string>();
        foreach (var projectFile in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(projectFile)!;
            if (
                directory.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || directory.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            )
            {
                continue;
            }

            var project = Path.GetFileNameWithoutExtension(projectFile);
            var leaks = ProjectPackageClosure(projectFile)
                .Where(package => package.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))
                .OrderBy(package => package, StringComparer.Ordinal);
            failures.AddRange(leaks.Select(package => $"{project} references '{package}'"));
        }

        Assert.True(
            failures.Count == 0,
            "EF Core leaked into the repository (it must be absent everywhere after the EF outbox package removal):\n"
                + string.Join("\n", failures)
        );
    }

    private static IEnumerable<string> ProjectPackageClosure(string projectFile)
    {
        var packages = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(projectFile);

        while (pending.Count > 0)
        {
            var path = Path.GetFullPath(pending.Pop());
            if (!File.Exists(path) || !visited.Add(path))
            {
                continue;
            }

            var document = XDocument.Load(path);
            foreach (var package in PackageReferenceNames(document))
            {
                packages.Add(package);
            }

            var projectDirectory = Path.GetDirectoryName(path)!;
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (!string.IsNullOrEmpty(include))
                {
                    pending.Push(Path.Combine(projectDirectory, include.Replace('\\', Path.DirectorySeparatorChar)));
                }
            }
        }

        return packages;
    }

    private static IEnumerable<string> PackageReferenceNames(XDocument project) =>
        project.Descendants("PackageReference").Select(reference => reference.Attribute("Include")?.Value).OfType<string>();

    [Fact]
    public void Source_layout_respects_feature_and_provider_ownership()
    {
        var repoRoot = ResolveRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src");
        var failures = new List<string>();

        if (Directory.Exists(Path.Combine(sourceRoot, "Acta", "Features")))
        {
            failures.Add("Acta contains a product Features folder.");
        }

        string[] horizontalNames = ["Api", "Services", "Rules", "Stores", "Delivery"];
        foreach (var projectRoot in Directory.EnumerateDirectories(sourceRoot))
        {
            var featuresRoot = Path.Combine(projectRoot, "Features");
            if (!Directory.Exists(featuresRoot))
            {
                continue;
            }

            failures.AddRange(
                Directory
                    .EnumerateDirectories(featuresRoot, "*", SearchOption.AllDirectories)
                    .Where(path => horizontalNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                    .Select(path => $"horizontal feature folder: {Relative(repoRoot, path)}")
            );

            var sharedRoot = Path.Combine(featuresRoot, "Shared");
            if (Directory.Exists(sharedRoot))
            {
                var rootNamespace = ProjectRootNamespace(projectRoot);
                var siblingUsing = new Regex(
                    @"^using\s+(?:global::)?" + Regex.Escape(rootNamespace) + @"\.Features\.(?!Shared(?:\.|;))",
                    RegexOptions.Multiline | RegexOptions.Compiled
                );
                foreach (var file in Directory.EnumerateFiles(sharedRoot, "*.cs", SearchOption.AllDirectories))
                {
                    if (siblingUsing.IsMatch(File.ReadAllText(file)))
                    {
                        failures.Add($"root Shared depends on a consuming feature: {Relative(repoRoot, file)}");
                    }
                }
            }
        }

        foreach (var provider in ProviderProjects)
        {
            var providerRoot = Path.Combine(sourceRoot, provider);
            // Stores are consolidated into shared Acta implementations; a provider owns no
            // store classes anywhere, only its dialect, options, bootstrap, migrations, and SQL.
            var stores = Directory.EnumerateFiles(providerRoot, "*Store.cs", SearchOption.AllDirectories).ToArray();
            failures.AddRange(stores.Select(path => $"provider retains a feature store after consolidation: {Relative(repoRoot, path)}"));
            failures.AddRange(
                Directory
                    .EnumerateFiles(providerRoot, "*Rule.cs", SearchOption.AllDirectories)
                    .Select(path => $"provider-independent rule lives in a provider: {Relative(repoRoot, path)}")
            );
        }

        Assert.True(failures.Count == 0, "Architecture source-layout violations:\n" + string.Join("\n", failures));
    }

    private static string ProjectRootNamespace(string projectRoot)
    {
        var projectFile = Directory.EnumerateFiles(projectRoot, "*.csproj").Single();
        var project = XDocument.Load(projectFile);
        return project.Descendants("RootNamespace").Select(element => element.Value).FirstOrDefault()
            ?? Path.GetFileNameWithoutExtension(projectFile);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Acta.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate Acta.slnx from " + AppContext.BaseDirectory);
    }
}
