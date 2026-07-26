using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Relational.Resources;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Schema;

public sealed class ProviderOperatorViewCatalogTests
{
    private static readonly string[] ExpectedViews =
    [
        "alerts_view",
        "checkpoints_view",
        "definitions_view",
        "events_view",
        "jobs_view",
        "schedules_view",
        "steps_view",
        "tags_view",
        "workers_view",
    ];

    private static readonly string[] ProviderAssemblies = ["Acta.Postgres", "Acta.SqlServer", "Acta.Sqlite"];

    private static readonly string[] DecodeKinds =
    [
        "actor",
        "alert-delivery-status",
        "alert-kind",
        "alert-severity",
        "event",
        "execution-status",
        "job-alert-profile",
        "alert-origin",
        "job-audit-level",
        "job-checkpoint-kind",
        "job-checkpoint-state",
        "job-deadline-behavior",
        "job-definition-status",
        "job-event-reason",
        "job-status",
        "job-step-state",
        "misfire-strategy",
        "priority",
        "schedule-expression-kind",
        "schedule-origin",
        "schedule-status",
        "tag-scope",
        "worker-status",
    ];

    [Fact(DisplayName = "Every provider owns the curated plural _view set")]
    public void Every_provider_owns_the_curated_plural_view_set()
    {
        foreach (var assemblyName in ProviderAssemblies)
        {
            var views = Catalog(assemblyName).Views().Select(static v => v.Name).OrderBy(static n => n, StringComparer.Ordinal);
            Assert.Equal(ExpectedViews, views);
        }
    }

    [Fact(DisplayName = "Core embeds no operator view SQL")]
    public void Core_embeds_no_operator_view_sql()
    {
        var resources = typeof(ActaStore).Assembly.GetManifestResourceNames();

        Assert.DoesNotContain(resources, static r => r.EndsWith(".view.sql", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Provider operator view bodies render without unresolved tokens")]
    public void Provider_view_bodies_render_without_unresolved_tokens()
    {
        foreach (var assemblyName in ProviderAssemblies)
        {
            var views = Catalog(assemblyName).Views().ToList();

            Assert.Equal(ExpectedViews.Length, views.Count);
            Assert.All(views, static v => Assert.DoesNotContain("{{", v.Body, StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "jobs_view" && v.Body.Contains("input_text", StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "jobs_view" && v.Body.Contains("tenant_key", StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "jobs_view" && v.Body.Contains("last_result_text", StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "checkpoints_view" && v.Body.Contains("value_text", StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "events_view" && v.Body.Contains("detail_text", StringComparison.Ordinal));
            Assert.Contains(views, static v => v.Name == "steps_view" && v.Body.Contains("result_text", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "Decode CASE snippets cover every code family used by operator views")]
    public void Decode_case_snippets_cover_view_code_families()
    {
        var availableKinds = CodeManifests.All.Select(e => e.CodeKind).ToHashSet(StringComparer.Ordinal);

        foreach (var kind in DecodeKinds)
        {
            Assert.Contains(kind, availableKinds);
            var sql = CodeDecodeSql.Case(kind, "x.code");
            Assert.StartsWith("CASE x.code WHEN ", sql, StringComparison.Ordinal);
            Assert.Contains(" THEN '", sql, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "Documented operator view names match the provider catalogs")]
    public void Documented_operator_view_names_match_catalogs()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var docs = string.Join(
            "\n",
            File.ReadAllText(Path.Combine(root, "docs", "quickstart.md")),
            File.ReadAllText(Path.Combine(root, "docs", "guide", "operator-guide.md")),
            File.ReadAllText(Path.Combine(root, "docs", "guide", "sql-recipes.md"))
        );
        var documented = Regex
            .Matches(docs, @"\bacta\.([a-z_]+_view)\b", RegexOptions.CultureInvariant)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(documented.Except(ExpectedViews, StringComparer.Ordinal));
        Assert.Empty(ExpectedViews.Except(documented, StringComparer.Ordinal));
    }

    private static SqlResourceCatalog Catalog(string assemblyName) => new(Assembly.Load(assemblyName), "acta");
}
