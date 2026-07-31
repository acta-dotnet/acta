using System.Globalization;
using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance;

/// <summary>
/// Generates and verifies docs/reference/conformance-contracts.md from conformance specs, store
/// contracts, and provider-owned SQL resources.
/// </summary>
public sealed class DocsContractTests
{
    private static string Generate()
    {
        var specs = typeof(IConformanceFixture)
            .Assembly.GetTypes()
            .Where(HarnessReflection.IsCandidateContractSpec)
            .Select(t => (Type: t, Attr: HarnessReflection.ConformanceSpec(t)))
            .Where(x => x.Attr is not null)
            .Select(x =>
                (
                    x.Type,
                    Attr: x.Attr!,
                    Guarantees: HarnessReflection.Guarantees(x.Type),
                    StoreMethods: x.Type.GetCustomAttributes(typeof(CoversStoreMethodAttribute), inherit: false)
                        .Cast<CoversStoreMethodAttribute>()
                        .Select(attribute => attribute.Identity)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(identity => identity, StringComparer.Ordinal)
                        .ToArray()
                )
            )
            .OrderBy(x => x.Attr.Area)
            .ThenBy(x => x.Attr.Id)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Conformance Contracts");
        sb.AppendLine();
        sb.AppendLine(
            "> Generated from `[ConformanceSpec]` / `[CoversStoreMethod]`, internal store contracts, and provider-owned SQL resources. Do not edit by hand. Regenerate:"
        );
        sb.AppendLine("> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests/Acta.Tests.csproj --filter DocsContractTests`.");
        sb.AppendLine();
        string? area = null;
        foreach (var (_, a, guarantees, storeMethods) in specs)
        {
            if (a.Area != area)
            {
                area = a.Area;
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {area}");
                sb.AppendLine();
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"### {a.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Contract:** {a.Contract}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Arrange:** {a.Arrange}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Act:** {a.Act}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Assert:** {a.Assert}");
            if (guarantees.Count > 0)
            {
                sb.AppendLine("- **Guarantees:**");
                foreach (var g in guarantees)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {g}");
                }
            }
            if (storeMethods.Length > 0)
            {
                sb.AppendLine("- **Store methods:**");
                foreach (var storeMethod in storeMethods)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - `{storeMethod}`");
                }
            }

            sb.AppendLine();
        }

        EmitPersistenceInventory(sb, specs);

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static void EmitPersistenceInventory(
        StringBuilder sb,
        IReadOnlyList<(Type Type, ConformanceSpecAttribute Attr, IReadOnlyList<string> Guarantees, string[] StoreMethods)> specs
    )
    {
        sb.AppendLine("## Persistence inventory");
        sb.AppendLine();
        sb.AppendLine(
            "The durable inventory is keyed by semantic store-contract methods and provider-owned logical SQL resources. "
                + "Operation classes and core SQL resources are not inventory sources."
        );
        sb.AppendLine();
        sb.AppendLine("### Store contract methods");
        sb.AppendLine();
        sb.AppendLine("| Store method | Covering conformance specs |");
        sb.AppendLine("| --- | --- |");

        var coveringSpecs = specs
            .SelectMany(spec => spec.StoreMethods.Select(method => (Method: method, spec.Attr.Title)))
            .GroupBy(item => item.Method, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .Select(item => item.Title)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(title => title, StringComparer.Ordinal)
                        .ToArray(),
                StringComparer.Ordinal
            );

        var storeMethods = StoreContractCoverageTests
            .StoreInterfaces()
            .SelectMany(store => StoreContractCoverageTests.DeclaredMethods(store).Select(method => (Store: store, Method: method)))
            .OrderBy(item => item.Store.FullName, StringComparer.Ordinal)
            .ThenBy(item => item.Method.Name, StringComparer.Ordinal);
        foreach (var (Store, Method) in storeMethods)
        {
            var identity = $"{Store.FullName}.{Method.Name}";
            var covers = coveringSpecs.TryGetValue(identity, out var titles) ? string.Join("<br>", titles.Select(EscapeCell)) : "·";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{Store.Name}.{Method.Name}` | {covers} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Provider SQL resources");
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

    private static string EscapeCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

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
