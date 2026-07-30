using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Pins the architecture rule that source namespaces follow project roots and physical folders,
/// with deliberate exceptions. The Acta SDK project's whole public surface lives in the flat
/// <c>Acta</c> namespace so consumers need a single using; folders there group by capability only.
/// Registration surface (<c>*Extensions.cs</c> at a project root: <c>AddActa</c>/<c>Use*</c>/
/// <c>MapActa</c>) sits at the package root for discoverability and declares <c>Acta</c> so
/// <c>using Acta;</c> alone lights it up - provider choice is a package reference, not a using.
/// Internal machinery left in <c>Hosting</c> folders collapses to the same entry namespace
/// (<c>Acta</c> for product assemblies, the root namespace for Acta.Testing).
/// </summary>
public sealed class NamespaceConventionTests
{
    private static readonly Regex NamespaceRx = new(@"^namespace\s+([^\s;{]+)", RegexOptions.Multiline | RegexOptions.Compiled);

    private const string FlatNamespaceProject = "Acta";

    [Fact]
    public void Source_namespaces_follow_project_roots_and_physical_folders()
    {
        var repoRoot = ResolveRepoRoot();
        var sourceRoot = Path.Combine(repoRoot, "src");
        var failures = new List<string>();
        var inspected = 0;

        foreach (var projectDirectory in Directory.EnumerateDirectories(sourceRoot))
        {
            var projectFile = Directory.EnumerateFiles(projectDirectory, "*.csproj").SingleOrDefault();
            if (projectFile is null)
            {
                continue;
            }

            var project = XDocument.Load(projectFile);
            var rootNamespace =
                project.Descendants("RootNamespace").Select(e => e.Value).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(projectFile);

            foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(projectDirectory, file);
                if (
                    relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("DashboardApp" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.GetFileName(file) == "IsExternalInit.cs"
                )
                {
                    continue;
                }

                var match = NamespaceRx.Match(File.ReadAllText(file));
                if (!match.Success)
                {
                    continue;
                }

                inspected++;
                var relativeDirectory = Path.GetDirectoryName(relative);
                var isEntryNamespaceFile =
                    relativeDirectory == "Hosting"
                    || (string.IsNullOrEmpty(relativeDirectory) && relative.EndsWith("Extensions.cs", StringComparison.Ordinal));
                var expected = isEntryNamespaceFile
                    ? Path.GetFileName(projectDirectory) == "Acta.Testing"
                        ? rootNamespace
                        : "Acta"
                    : Path.GetFileName(projectDirectory) == FlatNamespaceProject || string.IsNullOrEmpty(relativeDirectory)
                        ? rootNamespace
                        : rootNamespace
                            + "."
                            + relativeDirectory.Replace(Path.DirectorySeparatorChar, '.').Replace(Path.AltDirectorySeparatorChar, '.');
                if (match.Groups[1].Value != expected)
                {
                    failures.Add($"{Path.GetRelativePath(repoRoot, file)}: namespace is '{match.Groups[1].Value}', expected '{expected}'");
                }
            }
        }

        Assert.True(inspected > 0, "No source namespaces were inspected.");
        Assert.True(failures.Count == 0, "Namespace convention violations:\n" + string.Join("\n", failures));
    }

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

        throw new InvalidOperationException(
            "NamespaceConventionTests could not locate Acta.slnx marking the repo root from " + AppContext.BaseDirectory
        );
    }
}
