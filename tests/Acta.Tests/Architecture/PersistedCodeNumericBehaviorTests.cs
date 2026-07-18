using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

public sealed class PersistedCodeNumericBehaviorTests
{
    [Fact]
    public void Production_code_does_not_order_persisted_enum_members()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var enumNames = typeof(JobStatusCode)
            .Assembly.GetTypes()
            .Where(type => type.IsEnum && type.GetCustomAttributes(typeof(CodeKindAttribute), false).Length != 0)
            .Select(type => Regex.Escape(type.Name))
            .OrderByDescending(name => name.Length)
            .ToArray();
        var enumToken = $"(?:{string.Join("|", enumNames)})\\.[A-Za-z][A-Za-z0-9_]*";
        var comparison = new Regex($"(?:{enumToken})\\s*[<>]=?|[<>]=?\\s*(?:{enumToken})", RegexOptions.CultureInvariant);
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (Match match in comparison.Matches(text))
            {
                var line = 1 + text.AsSpan(0, match.Index).Count('\n');
                failures.Add($"{Path.GetRelativePath(root, path)}:{line}: {match.Value}");
            }
        }

        Assert.Empty(failures);
    }
}
