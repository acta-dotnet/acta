using System.Globalization;
using System.Text;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Generates and verifies docs/reference/conformance-contracts.md from conformance specs and
/// provider-owned SQL resources.
/// </summary>
public sealed class DocsContractTests
{
    private static string Generate()
    {
        var specs = ConformanceSpecSources
            .All.Where(s => s.Contract is not null)
            .Select(s => (Attr: s.Contract!, s.Guarantees))
            .OrderBy(x => x.Attr.Area)
            .ThenBy(x => x.Attr.Id)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Conformance Contracts");
        sb.AppendLine();
        sb.AppendLine("> Generated from `[ConformanceSpec]` and provider-owned SQL resources. Do not edit by hand. Regenerate:");
        sb.AppendLine("> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests`.");
        sb.AppendLine();
        string? area = null;
        foreach (var (a, guarantees) in specs)
        {
            if (a.Area != area)
            {
                area = a.Area;
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {area}");
                sb.AppendLine();
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"### {a.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Contract:** {a.Contract}");
            if (guarantees.Count > 0)
            {
                sb.AppendLine("- **Guarantees:**");
                foreach (var g in guarantees)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {g}");
                }
            }

            sb.AppendLine();
        }

        EmitSqlResources(sb);

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static void EmitSqlResources(StringBuilder sb)
    {
        sb.AppendLine("## Provider SQL resources");
        sb.AppendLine();
        sb.AppendLine("| Logical resource | PostgreSQL | SQL Server | SQLite |");
        sb.AppendLine("| --- | --- | --- | --- |");

        var inventories = ProviderResourceParityTests.Dialects.ToDictionary(
            dialect => dialect,
            dialect => ProviderResourceParityTests.LogicalResources(dialect).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        var resources = inventories
            .Values.SelectMany(resources => resources)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var cells = ProviderResourceParityTests
                .Dialects.Select(dialect => inventories[dialect].Contains(resource) ? "yes" : "·")
                .ToArray();
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{resource}` | {cells[0]} | {cells[1]} | {cells[2]} |");
        }

        sb.AppendLine();
    }

    private static string DocPath() => Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "conformance-contracts.md");

    /// <summary>
    /// Emits the doc when ACTA_EMIT_DOCS=1, then always asserts the committed file matches the specs.
    /// </summary>
    [Fact]
    public void Committed_doc_matches_the_specs()
    {
        var path = DocPath();
        var generated = Generate();

        if (Environment.GetEnvironmentVariable("ACTA_EMIT_DOCS") == "1")
        {
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), "docs/reference/conformance-contracts.md missing: run with ACTA_EMIT_DOCS=1.");
        var actual = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.True(
            actual == generated,
            "docs/reference/conformance-contracts.md is stale. Regenerate: ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests"
        );
    }
}
