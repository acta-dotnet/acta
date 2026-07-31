namespace Acta.Emit.Features.Docs;

/// <summary>Resolves each generated code-family entry to its owning <c>src/Acta</c> folder.</summary>
internal static class CodeFamilySourceLocator
{
    internal static IReadOnlyDictionary<string, string> ResolveAreas(string repoRoot, IEnumerable<string> familyNames)
    {
        var actaRoot = Path.Combine(repoRoot, "src", "Acta");
        if (!Directory.Exists(actaRoot))
        {
            throw new DirectoryNotFoundException($"Acta source directory not found: {actaRoot}");
        }

        var names = familyNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var requested = names.ToHashSet(StringComparer.Ordinal);
        var filesByTypeName = Directory
            .EnumerateFiles(actaRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => requested.Contains(SourceTypeName(path)))
            .GroupBy(SourceTypeName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal
            );

        var areas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!filesByTypeName.TryGetValue(name, out var matches) || matches.Length == 0)
            {
                throw new InvalidOperationException($"No source file found for code family '{name}' under {actaRoot}.");
            }
            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple source files found for code family '{name}': {string.Join(", ", matches.Select(path => Path.GetRelativePath(repoRoot, path)))}."
                );
            }

            var relativePath = Path.GetRelativePath(actaRoot, matches[0]);
            var parts = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            );
            if (parts.Length != 2 || !string.Equals(parts[1], name + ".cs", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Code family '{name}' must be declared at src/Acta/<Area>/{name}.cs; found {Path.GetRelativePath(repoRoot, matches[0])}."
                );
            }

            areas.Add(name, parts[0]);
        }

        return areas;
    }

    private static string SourceTypeName(string path) =>
        Path.GetFileNameWithoutExtension(path) ?? throw new InvalidOperationException($"Source file has no type name: {path}");
}
