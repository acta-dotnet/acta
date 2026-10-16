using System.Globalization;
using System.Text.RegularExpressions;
using Acta.Emit.Shared;

namespace Acta.Emit.Features.Migrations;

/// <summary>
/// Filesystem conventions for the committed <c>M{yyyyMMddHHmmss}_{name}.sql</c> migrations. Each provider owns its
/// migrations under its own package (<c>src/Acta.{Provider}/Migrations</c>) with bare, suffix-free names (the
/// package is the dialect). The UTC instant in the stem is the order, so nothing here counts.
/// </summary>
internal static partial class MigrationFiles
{
    private static string Dir(string repoRoot, string token) =>
        Path.Combine(repoRoot, "src", ProviderCatalog.ByToken(token).Project, "Migrations");

    /// <summary>A provider's migration stems in apply order, which is ordinal order.</summary>
    internal static IReadOnlyList<string> Stems(string repoRoot, string token)
    {
        var dir = Dir(repoRoot, token);
        return Directory.Exists(dir)
            ?
            [
                .. Directory
                    .EnumerateFiles(dir, "M*.sql")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Order(StringComparer.Ordinal),
            ]
            : [];
    }

    internal static IEnumerable<string> AllMigrationSql(string repoRoot) =>
        ProviderCatalog.All.SelectMany(p => Stems(repoRoot, p.Token).Select(s => PathFor(repoRoot, p.Token, s)));

    internal static string Stem(DateTime utc, string name) => $"M{utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{name}";

    /// <summary>The name part of a stem: what follows <c>M{yyyyMMddHHmmss}_</c>.</summary>
    internal static string NameOf(string stem) => stem["M00000000000000_".Length..];

    internal static string PathFor(string repoRoot, string token, string stem) => Path.Combine(Dir(repoRoot, token), stem + ".sql");

    internal static bool IsValidName(string name) => NameRegex().IsMatch(name);

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
