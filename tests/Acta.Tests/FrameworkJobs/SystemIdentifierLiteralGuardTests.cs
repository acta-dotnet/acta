using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.FrameworkJobs;

public sealed class SystemIdentifierLiteralGuardTests
{
    private static readonly string[] ForbiddenStoredLiterals =
    [
        "acta." + "child.",
        "acta\\" + ".child",
        "acta." + "progress",
        "acta_" + "version",
        "acta" + "Version",
        "Acta" + "Version",
        "framework" + ":",
        "framework" + "-critical",
    ];

    private static readonly string[] GuardedRoots = ["src", "tests", "docs", "concepts", "tools", "demos", "support", "anvil"];

    [Fact]
    public void Source_docs_and_tests_do_not_reintroduce_old_system_owned_literals()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var files = GuardedRoots
            .Select(path => Path.Combine(root, path))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            .Where(IsGuardedTextFile)
            .ToArray();

        var hits = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var literal in ForbiddenStoredLiterals)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    hits.Add($"{Path.GetRelativePath(root, file)} contains {literal}");
                }
            }
        }

        Assert.Empty(hits);
    }

    private static bool IsGuardedTextFile(string file)
    {
        if (
            file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || file.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        )
        {
            return false;
        }

        return Path.GetExtension(file) is ".cs" or ".sql" or ".md" or ".json";
    }
}
