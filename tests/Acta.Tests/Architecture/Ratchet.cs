using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds a rule's known violations in <c>tests/ratchets/&lt;rule&gt;.txt</c>, a list that may only shrink. A new
/// violation fails, and so does an entry that no longer occurs. A missing file is written from the current
/// violations and fails once, so a new or regenerated baseline shows up in review. Keys name types, members, file
/// names, or SQL stems rather than folders, so they survive the moves that reorganise <c>src/</c>.
/// </summary>
internal static class Ratchet
{
    public static void Check(string rule, IEnumerable<string> violations, string fix) =>
        Check(rule, violations.Select(key => (key, (string?)null)), fix);

    /// <summary>The detail is shown for a new violation only; the file holds the key.</summary>
    public static void Check(string rule, IEnumerable<(string Key, string? Detail)> violations, string fix)
    {
        var relative = $"tests/ratchets/{rule}.txt";
        var path = Path.Combine(IntegrationConfig.FindRepoRoot(), relative);
        var details = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, detail) in violations)
        {
            details.TryAdd(key, detail);
        }
        var current = details.Keys.Order(StringComparer.Ordinal).ToList();

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"# Known {rule} violations; entries may only be removed.\n" + string.Concat(current.Select(v => v + "\n"))
            );
            Assert.Fail($"Wrote {relative} with {current.Count} entries. Review it and run the test again.");
        }

        var baseline = File.ReadLines(path).Where(line => line.Length > 0 && line[0] != '#').ToHashSet(StringComparer.Ordinal);
        var added = current.Except(baseline).ToList();
        var gone = baseline.Except(current).Order(StringComparer.Ordinal).ToList();

        var message = new List<string>();
        if (added.Count > 0)
        {
            message.Add($"{added.Count} new {rule} violation(s). {fix}");
            message.AddRange(added.Select(key => details[key] is { } detail ? $"  + {key}: {detail}" : $"  + {key}"));
        }
        if (gone.Count > 0)
        {
            message.Add($"{gone.Count} entry(ies) in {relative} no longer occur. Delete them, or delete the file and run the test.");
            message.AddRange(gone.Select(key => "  - " + key));
        }
        Assert.True(message.Count == 0, string.Join("\n", message));
    }
}
