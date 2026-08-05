using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

public sealed class CanonicalVocabularyTests
{
    private static readonly string[] SourceRoots = ["src", "tests", "tools", "docs", "concepts", "demos", "anvil"];
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".sql",
        ".md",
        ".json",
        ".ts",
        ".svelte",
    };

    [Fact(DisplayName = "Repository contracts and SQL contain no superseded naming vocabulary")]
    public void Repository_contains_only_canonical_frozen_names()
    {
        var forbidden = new[]
        {
            "Actor" + "Id",
            "actor" + "Id",
            "actor" + "_id",
            "Job" + "Tag",
            "Job" + "TagInput",
            "Job" + "TagFilter",
            "Sys" + "Setting",
            "JobAlert" + "SourceCode",
            "Schedule" + "SourceCode",
            "source" + "_code",
            "job-alert-" + "source",
            "schedule-" + "source",
            "job-alert-" + "origin",
            // 0.4.0 retired the "metadata" notion: the verb names the entity, and the catalog
            // descriptive fields are just fields. Nothing in the model is called metadata any more.
            "CatalogMetadata" + "Limits",
            "CatalogMetadata" + "Validation",
            "UpdateNamespace" + "Metadata",
            "UpdateTenant" + "Metadata",
            "UpdateMetadata" + "Async",
            "update_namespace" + "_metadata",
            "update_tenant" + "_metadata",
            "NamespaceMetadata" + "PatchRequest",
            "TenantMetadata" + "PatchRequest",
            "NamespaceMetadata" + "Changed",
            "TenantMetadata" + "Changed",
            "namespace." + "metadata-changed",
            "tenant." + "metadata-changed",
            "ValidateMetadata" + "Length",
        };

        var root = IntegrationConfig.FindRepoRoot();
        var failures = new List<string>();
        foreach (var sourceRoot in SourceRoots)
        {
            var directory = Path.Combine(root, sourceRoot);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!Extensions.Contains(Path.GetExtension(file)) || IsBuildArtifact(file))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var term in forbidden)
                {
                    if (text.Contains(term, StringComparison.Ordinal))
                    {
                        failures.Add($"{Path.GetRelativePath(root, file)}: {term}");
                    }
                }
            }
        }

        Assert.Empty(failures.OrderBy(static x => x, StringComparer.Ordinal));
    }

    // Skips content that is not part of the repository. Build output is the obvious case; docs/superpowers
    // is the other one: it is git-excluded working material, so a design note there that quotes a retired
    // name as an example is not the codebase using it. The gate guards committed sources.
    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}superpowers{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
