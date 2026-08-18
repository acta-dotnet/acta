using System.Collections.Concurrent;
using System.Reflection;
using Acta.Runtime.Hosting;

namespace Acta.Relational.Resources;

/// <summary>
/// Convention-based catalog of one provider assembly's executable SQL resources, all under a single
/// <c>Sql/{Capability}/{Operation}.sql</c> root (schema commands live at <c>Sql/Schema/</c>; ordered
/// DDL migrations are not catalog resources and stay under <c>Schema/Migrations/</c>). Resources are
/// embedded with the assembly-name-prefixed dotted logical name. Each provider owns its complete
/// executable SQL set, so lookups never fall back across dialects or assemblies; a missing resource
/// is a provider defect and throws. Rendered text substitutes <c>{{schema}}</c>, <c>{{now}}</c> (the
/// SQLite epoch-milliseconds instant encoding; only SQLite bodies carry the token), and
/// <c>{{decode:...}}</c> tokens, cached per resource for the process lifetime.
/// </summary>
internal sealed class SqlResourceCatalog
{
    private readonly Assembly _assembly;
    private readonly string _prefix;
    private readonly string? _schema;
    private readonly string? _table;
    private readonly HashSet<string> _resources;
    private readonly ConcurrentDictionary<string, string> _rendered = new(StringComparer.Ordinal);

    public SqlResourceCatalog(Assembly assembly, string? schema, string? table = null)
    {
        _assembly = assembly;
        _prefix = assembly.GetName().Name + ".";
        _schema = schema;
        _table = table;
        _resources = assembly
            .GetManifestResourceNames()
            .Where(n =>
                n.StartsWith(_prefix, StringComparison.Ordinal)
                && n.EndsWith(".sql", StringComparison.Ordinal)
                && n.AsSpan(_prefix.Length).StartsWith("Sql.", StringComparison.Ordinal)
            )
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Rendered SQL for a capability-local resource path such as <c>Sql/Overview/GetOverview.sql</c>.
    /// </summary>
    public string Load(string path) => Render(_prefix + path.Replace('/', '.'));

    /// <summary>
    /// The provider's feature-local routine bodies (<c>*.routine.sql</c>), each paired with its
    /// snake_case routine name, for installation after migrations. Empty for inline-only providers.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Routines()
    {
        var routines = _resources.Where(r => r.EndsWith(".routine.sql", StringComparison.Ordinal)).OrderBy(r => r, StringComparer.Ordinal);
        foreach (var resource in routines)
        {
            var stem = resource[..^".routine.sql".Length];
            var operation = stem[(stem.LastIndexOf('.') + 1)..];
            yield return (System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(operation), Render(resource));
        }
    }

    /// <summary>
    /// The provider's operator-view SELECT bodies (<c>*.view.sql</c>), each paired with the
    /// snake_case database view name. Schema installation owns the provider-specific CREATE/DROP
    /// wrapper; the provider resource owns the executable SELECT body.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Views()
    {
        var views = _resources.Where(r => r.EndsWith(".view.sql", StringComparison.Ordinal)).OrderBy(r => r, StringComparer.Ordinal);
        foreach (var resource in views)
        {
            var stem = resource[..^".view.sql".Length];
            var view = stem[(stem.LastIndexOf('.') + 1)..];
            var name = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(view);
            yield return !name.EndsWith("_view", StringComparison.Ordinal)
                ? throw new InvalidOperationException($"Provider view resource '{resource}' must install as a plural '_view' name.")
                : ((string Name, string Body))(name, Render(resource));
        }
    }

    private string Render(string resourceName) =>
        _rendered.GetOrAdd(
            resourceName,
            name =>
            {
                if (!_resources.Contains(name))
                {
                    throw new InvalidOperationException($"Provider assembly '{_assembly.GetName().Name}' embeds no SQL resource '{name}'.");
                }

                using var stream = _assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                // {{table}} and {{table_ref}} are substituted only for the external-outbox source catalog,
                // whose commands target a producer-owned table whose name is configurable; ledger resources
                // never use them. {{table_ref}} is the DML table reference: schema-qualified when a schema
                // override is supplied, otherwise the bare table so the database default schema (the login
                // default on SQL Server, the search_path first match on PostgreSQL) resolves it.
                var table = _table ?? "acta_outbox";
                // The single home of the outbox schema/table concatenation (also validates the identifiers,
                // already-validated upstream so it never throws here for a ledger catalog).
                var tableRef = OutboxIdentifier.Qualify(table, _schema);
                return CodeDecodeSql.RenderDecodeTokens(
                    reader
                        .ReadToEnd()
                        .Replace("{{schema}}", _schema ?? "", StringComparison.Ordinal)
                        .Replace("{{table_ref}}", tableRef, StringComparison.Ordinal)
                        .Replace("{{table}}", table, StringComparison.Ordinal)
                        .Replace("{{now}}", "CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)", StringComparison.Ordinal)
                );
            }
        );
}
