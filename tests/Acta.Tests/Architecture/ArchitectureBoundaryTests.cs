using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>Locks the dependency and source-layout boundaries established by ARCHITECTURE.md.</summary>
public sealed class ArchitectureBoundaryTests
{
    private static readonly string[] ProviderProjects = ["Acta.Postgres", "Acta.SqlServer", "Acta.Sqlite"];

    [Fact]
    public void Core_store_ports_are_internal_and_provider_neutral()
    {
        var core = typeof(IActaStore).Assembly;
        var relationalAssemblyName = typeof(IEntity).Assembly.GetName().Name;
        var references = core.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain(relationalAssemblyName, references);
        Assert.DoesNotContain(core.GetManifestResourceNames(), name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        var composite = typeof(IActaStore);
        var storePorts = composite.GetProperties().Select(property => property.PropertyType).ToArray();
        Assert.NotEmpty(storePorts);
        Assert.False(composite.IsPublic);
        Assert.DoesNotContain(composite.GetMethods(), method => !method.IsSpecialName);
        Assert.All(composite.GetProperties(), property => Assert.True(property.CanRead && !property.CanWrite));
        Assert.All(storePorts, store => Assert.False(store.IsPublic, $"{store.FullName} must remain internal."));

        var relationalLeaks = storePorts
            .SelectMany(store => store.GetMethods())
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .SelectMany(TypeClosure)
            .Where(type => type.Assembly.GetName().Name == relationalAssemblyName)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            relationalLeaks.Length == 0,
            "Store contracts expose relational types, preventing a non-relational provider:\n" + string.Join("\n", relationalLeaks)
        );

        var coreStoreImplementations = core.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && storePorts.Any(store => store.IsAssignableFrom(type)))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            coreStoreImplementations.Length == 0,
            "Provider-independent core contains store implementations:\n" + string.Join("\n", coreStoreImplementations)
        );
    }

    [Fact]
    public void Relational_implementation_surface_is_internal()
    {
        var relational = typeof(IEntity).Assembly;
        var references = relational.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.Contains("Acta.Contracts", references);
        Assert.Contains("Acta", references);

        Assert.False(typeof(IDbSession).IsPublic);
        Assert.False(typeof(IEntity).IsPublic);
        Assert.False(typeof(IEntity<>).IsPublic);
        Assert.False(typeof(DbKind).IsPublic);

        string[] implementationNamespaces =
        [
            "Acta.Relational.Commands",
            "Acta.Relational.Entities",
            "Acta.Relational.Resources",
            "Acta.Relational.Schema",
            "Acta.Relational.Stores",
        ];
        var exposed = relational
            .GetExportedTypes()
            .Where(type =>
                type.Namespace is { } ns
                && implementationNamespaces.Any(prefix => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
            )
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.True(exposed.Length == 0, "Relational implementation types are public:\n" + string.Join("\n", exposed));
    }

    [Fact]
    public void Source_layout_respects_feature_and_provider_ownership()
    {
        var repoRoot = ResolveRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src");
        var coreRoot = Path.Combine(sourceRoot, "Acta");
        var failures = new List<string>();

        string[] obsoleteCoreFolders =
        [
            "Operations",
            "Modules",
            "SystemJobs",
            "Runtime",
            "Storage",
            "Entities",
            "Schema",
            "Builders",
            "Errors",
        ];
        failures.AddRange(
            obsoleteCoreFolders
                .Select(folder => Path.Combine(coreRoot, folder))
                .Where(Directory.Exists)
                .Select(path => $"obsolete core folder remains: {Relative(repoRoot, path)}")
        );
        failures.AddRange(
            Directory
                .EnumerateFiles(coreRoot, "*.sql", SearchOption.AllDirectories)
                .Select(path => $"core executable SQL remains: {Relative(repoRoot, path)}")
        );
        if (Directory.Exists(Path.Combine(sourceRoot, "Acta.Relational", "Features")))
        {
            failures.Add("Acta.Relational contains a product Features folder.");
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
            var featureRoot = Path.Combine(providerRoot, "Features");
            // Stores are consolidated into shared Acta.Relational implementations; a provider owns no
            // feature-store classes, only dialects, bootstrap, and SQL.
            var stores = Directory.EnumerateFiles(featureRoot, "*Store.cs", SearchOption.AllDirectories).ToArray();
            failures.AddRange(stores.Select(path => $"provider retains a feature store after consolidation: {Relative(repoRoot, path)}"));
            failures.AddRange(
                Directory
                    .EnumerateFiles(providerRoot, "*Rule.cs", SearchOption.AllDirectories)
                    .Select(path => $"provider-independent rule lives in a provider: {Relative(repoRoot, path)}")
            );

            var sqlFiles = Directory.EnumerateFiles(providerRoot, "*.sql", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(sqlFiles);
            foreach (var sqlFile in sqlFiles)
            {
                var path = Relative(providerRoot, sqlFile);
                var featureSql = Regex.IsMatch(path, @"^Features/[^/]+/Sql/.+\.sql$", RegexOptions.CultureInvariant);
                var serviceSql = Regex.IsMatch(path, @"^Services/(Locks|Time)/Sql/.+\.sql$", RegexOptions.CultureInvariant);
                var schemaSql = Regex.IsMatch(path, @"^Schema/Sql/.+\.sql$", RegexOptions.CultureInvariant);
                var migrationSql = Regex.IsMatch(path, @"^Schema/Migrations/.+\.sql$", RegexOptions.CultureInvariant);
                if (!featureSql && !serviceSql && !schemaSql && !migrationSql)
                {
                    failures.Add($"provider SQL is outside an owned resource folder: {Relative(repoRoot, sqlFile)}");
                }
            }
        }

        Assert.True(failures.Count == 0, "Architecture source-layout violations:\n" + string.Join("\n", failures));
    }

    private static IEnumerable<Type> TypeClosure(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var element in TypeClosure(type.GetElementType()!))
            {
                yield return element;
            }
        }
        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (var nested in TypeClosure(argument))
            {
                yield return nested;
            }
        }
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
