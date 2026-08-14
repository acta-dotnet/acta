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
            // 0.4.0 aligned the event names on two segments and named modification events for the
            // Update* call that causes them. The bare status words (the old terminal-success and
            // in-flight spellings) are ordinary English and cannot be pinned here.
            "job.execution" + ".started",
            "job.execution" + ".finished",
            "job.recurring" + ".rolled-over",
            "job.signal" + ".raised",
            "definition." + "policy-changed",
            "schedule." + "overrides-changed",
            "JobDefinition" + "PolicyChanged",
            "ScheduleOverrides" + "Changed",
            "SetOverrides" + "Async",
            "job." + "other",
            // 0.9.0 restored noun.past-participle on the last two event strings, renamed the two
            // CLR-side family names whose slugs were right all along (the slugs stayed event/actor),
            // and normalized the misfire member. The bare old slug "priority" is ordinary English
            // and cannot be pinned here; its family slug moved to job-priority.
            // The retired note event string is a prefix of its replacement, and the retired member
            // name a prefix of WorkerDeadAfter, so both pins carry a delimiter that only the retired form produces.
            "job." + "note\"",
            "worker." + "dead",
            "WorkerDead" + " = 122",
            "fire-once-" + "catch-up",
            "FireOnce" + "CatchUp",
            "job-deadline-" + "behavior",
            // 0.9.0 renamed the named-lock table to locks and replaced the recycling version with a
            // per-hold token; the one-member kind family retired with the kind column.
            "LeaseKind" + "Code",
            "lease-" + "kind",
            "lease_" + "key",
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
        || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        // Certification seals are sworn transcripts of published runs and quote the vocabulary of
        // their day; design plans document renames, so the retired name is their subject matter.
        // Neither is ever edited to track a rename.
        || path.Contains($"docs{Path.DirectorySeparatorChar}certification{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"docs{Path.DirectorySeparatorChar}designs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
