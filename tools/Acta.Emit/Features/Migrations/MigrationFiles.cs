using System.Text.RegularExpressions;
using Acta.Emit.Shared.Sql;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Filesystem conventions for the committed <c>M{nnn}_{name}.sql</c> migrations. Each provider owns its
/// migrations under its own package (<c>src/Acta.{Provider}/Schema/Migrations</c>) with bare, suffix-free names
/// (the package is the dialect). Version numbers are global across providers (one counter scanning every
/// provider dir), so a late-joining provider shares the current release number rather than restarting at 1.
/// </summary>
internal static partial class MigrationFiles
{
    private static readonly string[] AllSuffixes = ["sqlite", "pg", "mssql"];

    private static string ProviderProject(string suffix) => Acta.Emit.Shared.ProviderCatalog.BySuffix(suffix).Project;

    private static string Dir(string repoRoot, string suffix) =>
        Path.Combine(repoRoot, "src", ProviderProject(suffix), "Schema", "Migrations");

    /// <summary>Highest M-number across every provider's files, or 0 when none exist.</summary>
    internal static int CurrentMaxVersion(string repoRoot)
    {
        var max = 0;
        foreach (var suffix in AllSuffixes)
        {
            var dir = Dir(repoRoot, suffix);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "M*.sql"))
            {
                if (TryVersion(Path.GetFileName(file), out var v) && v > max)
                {
                    max = v;
                }
            }
        }
        return max;
    }

    internal static int NextVersion(string repoRoot) => CurrentMaxVersion(repoRoot) + 1;

    /// <summary>True when this provider already has a migration below <paramref name="version"/>:
    /// i.e. it should get a delta, not a fresh full baseline.</summary>
    internal static bool ProviderHasFilesBelow(string repoRoot, string suffix, int version)
    {
        var dir = Dir(repoRoot, suffix);
        if (!Directory.Exists(dir))
        {
            return false;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "M*.sql"))
        {
            if (TryVersion(Path.GetFileName(file), out var v) && v < version)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The tip files for one provider at <paramref name="version"/> (any name → supports rename).</summary>
    internal static IEnumerable<string> TipFiles(string repoRoot, string suffix, int version)
    {
        var dir = Dir(repoRoot, suffix);
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(dir, $"M{version:D3}_*.sql"))
        {
            yield return file;
        }
    }

    /// <summary>Reads a provider's tip migration name back from its filename, or null if absent.</summary>
    internal static string? TipName(string repoRoot, string suffix, int version)
    {
        const string tail = ".sql";
        foreach (var file in TipFiles(repoRoot, suffix, version))
        {
            var leaf = Path.GetFileName(file);
            var inner = leaf[..^tail.Length]; // "M002_add_x"
            var underscore = inner.IndexOf('_', StringComparison.Ordinal);
            if (underscore >= 0)
            {
                return inner[(underscore + 1)..];
            }
        }
        return null;
    }

    internal static IEnumerable<string> AllMigrationSql(string repoRoot)
    {
        foreach (var suffix in AllSuffixes)
        {
            var dir = Dir(repoRoot, suffix);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(dir, "M*.sql"))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// The provider's full baseline migration: the one file of its history that writes the version-0
    /// stamp row. Null when the provider has no migrations yet.
    /// </summary>
    internal static string? BaselineFile(string repoRoot, string suffix)
    {
        var dir = Dir(repoRoot, suffix);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        return Directory
            .EnumerateFiles(dir, "M*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault(f => BaselineStamp.Recorded(File.ReadAllText(f)) is not null);
    }

    /// <summary>
    /// The stamp recorded in the provider's committed baseline migration, read from the file rather
    /// than from the generated constants: everything the emitter writes in one pass then agrees with
    /// the SQL of that same pass, without waiting for a rebuild to pick the constants up.
    /// </summary>
    internal static string? BaselineStampFor(string repoRoot, string suffix) =>
        BaselineFile(repoRoot, suffix) is { } path ? BaselineStamp.Recorded(File.ReadAllText(path)) : null;

    internal static string PathFor(string repoRoot, string suffix, int version, string name) =>
        Path.Combine(Dir(repoRoot, suffix), $"M{version:D3}_{name}.sql");

    internal static bool IsValidName(string name) => NameRegex().IsMatch(name);

    internal static string DefaultName(bool isGenesis) => isGenesis ? "init" : "change";

    private static bool TryVersion(string leaf, out int version)
    {
        version = 0;
        return leaf.Length >= 4 && leaf[0] == 'M' && int.TryParse(leaf.AsSpan(1, 3), out version);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
