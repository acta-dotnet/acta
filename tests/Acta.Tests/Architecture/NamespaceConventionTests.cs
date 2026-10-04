using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Pins one namespace per assembly: every source file declares its project's root namespace, whatever
/// folder it sits in, so a move between folders never touches a using. One exception: the core registration
/// entry point lives in <c>Acta</c> beside the public surface it registers.
/// </summary>
public sealed partial class NamespaceConventionTests
{
    private static readonly Regex NamespaceRx = MyRegex();

    [Fact]
    public void Source_namespaces_are_the_assembly_root()
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
                var isActaConsumerEntry =
                    Path.GetFileName(projectDirectory) == "Acta.Runtime" && relative == "ActaServiceCollectionExtensions.cs";
                var expected = isActaConsumerEntry ? "Acta" : rootNamespace;
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

    [GeneratedRegex(@"^namespace\s+([^\s;{]+)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
