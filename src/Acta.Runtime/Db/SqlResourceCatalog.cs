using System.Collections.Concurrent;
using System.Reflection;

namespace Acta.Runtime;

/// <summary>
/// Catalog of one provider assembly's executable SQL resources, all under a single <c>Sql/</c> root and
/// addressed by file stem: the stem is unique per provider, so a caller names <c>GetOverview</c> and never
/// the folder it lives in. Ordered DDL migrations are not catalog resources and stay under
/// <c>Schema/Migrations/</c>. Each provider owns its complete executable SQL set, so lookups never fall
/// back across dialects or assemblies; a missing resource is a provider defect and throws. Rendered text
/// substitutes <c>{{schema}}</c>, <c>{{now}}</c> (the SQLite epoch-milliseconds instant encoding; only
/// SQLite bodies carry the token), and <c>{{decode:...}}</c> tokens, cached per resource for the process
/// lifetime.
/// </summary>
internal sealed class SqlResourceCatalog
{
    private readonly Assembly _assembly;
    private readonly string? _schema;
    private readonly string? _table;
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rendered = new(StringComparer.Ordinal);

    public SqlResourceCatalog(Assembly assembly, string? schema, string? table = null)
    {
        _assembly = assembly;
        _schema = schema;
        _table = table;
        var prefix = assembly.GetName().Name + ".Sql.";
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var name = resource[..^".sql".Length];
            name =
                name.EndsWith(".routine", StringComparison.Ordinal) ? name[..^".routine".Length]
                : name.EndsWith(".view", StringComparison.Ordinal) ? name[..^".view".Length]
                : name;
            var stem = name[(name.LastIndexOf('.') + 1)..];
            if (!_resources.TryAdd(stem, resource))
            {
                throw new InvalidOperationException(
                    $"Provider assembly '{assembly.GetName().Name}' embeds two SQL resources with the stem '{stem}': '{_resources[stem]}' and '{resource}'."
                );
            }
        }
    }

    /// <summary>Rendered SQL for a stem such as <c>GetOverview</c>.</summary>
    public string Load(string stem) => Render(Resource(stem));

    /// <summary>Each command has exactly one provider-owned form; no provider-wide fallback.</summary>
    public StoreExecutionKind Resolve(StoreCommand command) =>
        Resource(command.Operation).EndsWith(".routine.sql", StringComparison.Ordinal)
            ? StoreExecutionKind.Routine
            : StoreExecutionKind.Inline;

    /// <summary>
    /// The provider's feature-local routine bodies (<c>*.routine.sql</c>), each paired with its
    /// snake_case routine name, for installation after migrations. Empty for inline-only providers.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Routines()
    {
        var routines = _resources
            .Where(r => r.Value.EndsWith(".routine.sql", StringComparison.Ordinal))
            .OrderBy(r => r.Value, StringComparer.Ordinal);
        foreach (var (stem, resource) in routines)
        {
            yield return (System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(stem), Render(resource));
        }
    }

    /// <summary>
    /// The provider's operator-view SELECT bodies (<c>*.view.sql</c>), each paired with the
    /// snake_case database view name. Schema installation owns the provider-specific CREATE/DROP
    /// wrapper; the provider resource owns the executable SELECT body.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Views()
    {
        var views = _resources
            .Where(r => r.Value.EndsWith(".view.sql", StringComparison.Ordinal))
            .OrderBy(r => r.Value, StringComparer.Ordinal);
        foreach (var (stem, resource) in views)
        {
            var name = System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(stem);
            yield return !name.EndsWith("_view", StringComparison.Ordinal)
                ? throw new InvalidOperationException($"Provider view resource '{resource}' must install as a plural '_view' name.")
                : ((string Name, string Body))(name, Render(resource));
        }
    }

    private string Resource(string stem) =>
        _resources.TryGetValue(stem, out var resource)
            ? resource
            : throw new InvalidOperationException($"Provider assembly '{_assembly.GetName().Name}' embeds no SQL for '{stem}'.");

    private string Render(string resourceName) =>
        _rendered.GetOrAdd(
            resourceName,
            name =>
            {
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
