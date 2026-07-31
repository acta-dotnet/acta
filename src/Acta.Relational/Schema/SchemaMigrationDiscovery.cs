using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Acta.Relational.Schema;

/// <summary>
/// Discovers a provider assembly's flat <c>Mnnn_*.sql</c> migration resources in ascending version
/// order. Provider ownership removes the old dialect-suffix fallback: each assembly embeds one
/// complete migration sequence.
/// </summary>
internal static partial class SchemaMigrationDiscovery
{
    [GeneratedRegex(@"^M([0-9]{3})_(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    public static IReadOnlyList<SchemaMigration> Discover(Assembly assembly)
    {
        var prefix = assembly.GetName().Name + ".Schema.Migrations.";
        var nameRegex = FileNameRegex();
        var found = new List<SchemaMigration>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var leaf = resource[prefix.Length..^".sql".Length];
            var match = nameRegex.Match(leaf);
            if (!match.Success)
            {
                continue;
            }

            var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var displayName = $"M{version:D3}_{match.Groups[2].Value}";
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            found.Add(new SchemaMigration(version, displayName, reader.ReadToEnd()));
        }

        var duplicate = found.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate migration version M{duplicate.Key:D3} in {assembly.GetName().Name}: "
                    + string.Join(", ", duplicate.Select(m => m.Name))
            );
        }

        return found.Count == 0
            ? throw new InvalidOperationException(
                $"No migration scripts found matching 'M{{nnn}}_*.sql' in {assembly.GetName().Name}. "
                    + "The provider package must embed its complete Schema/Migrations/*.sql sequence."
            )
            : [.. found.OrderBy(m => m.Version)];
    }
}
