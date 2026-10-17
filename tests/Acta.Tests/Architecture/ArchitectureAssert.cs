using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>Fails an architecture rule on any violation, listing each one under the fix that clears it.</summary>
internal static class ArchitectureAssert
{
    public static void NoViolations(IEnumerable<string> violations, string fix) =>
        NoViolations(violations.Select(key => (key, (string?)null)), fix);

    /// <summary>The detail, when there is one, follows its key in the failure message.</summary>
    public static void NoViolations(IEnumerable<(string Key, string? Detail)> violations, string fix)
    {
        var found = violations.DistinctBy(v => v.Key, StringComparer.Ordinal).OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        Assert.True(
            found.Count == 0,
            $"{found.Count} violation(s). {fix}\n"
                + string.Join("\n", found.Select(v => v.Detail is { } detail ? $"  {v.Key}: {detail}" : $"  {v.Key}"))
        );
    }
}
