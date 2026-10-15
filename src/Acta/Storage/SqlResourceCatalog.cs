using System.Collections.Concurrent;
using System.Reflection;

namespace Acta;

/// <summary>
/// Catalog of one dialect's inline SQL, embedded in <c>Acta</c> as <c>X.{dialect}.sql</c> beside its operation and addressed
/// by file stem, unique per dialect, so a caller names <c>GetOverview</c> and never its folder. Ordered DDL migrations are not
/// catalog resources and stay in the provider under <c>Migrations/</c>; routine and operator-view sources are not either,
/// because their bodies reach a database only inside a migration. Lookups never fall back across dialects; loading a stem with
/// no file throws. Rendered text substitutes <c>{{schema}}</c>, <c>{{now}}</c> (the SQLite epoch-milliseconds instant; only
/// SQLite bodies carry it), and <c>{{decode:...}}</c> tokens, cached per resource for the process lifetime.
/// </summary>
internal sealed class SqlResourceCatalog
{
    private readonly Assembly _assembly;
    private readonly string _dialect;
    private readonly string? _schema;
    private readonly string? _table;
    private readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rendered = new(StringComparer.Ordinal);

    public SqlResourceCatalog(string dialect, string? schema, string? table = null)
        : this(typeof(SqlResourceCatalog).Assembly, dialect, schema, table) { }

    /// <summary>A catalog over another assembly's <c>X.{dialect}.sql</c> resources, for test fixtures.</summary>
    internal SqlResourceCatalog(Assembly assembly, string dialect, string? schema, string? table = null)
    {
        _assembly = assembly;
        _dialect = dialect;
        _schema = schema;
        _table = table;
        var suffix = "." + dialect + ".sql";
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(suffix, StringComparison.Ordinal)))
        {
            var name = resource[..^suffix.Length];
            var stem = name[(name.LastIndexOf('.') + 1)..];
            if (!_resources.TryAdd(stem, resource))
            {
                throw new InvalidOperationException(
                    $"The {dialect} SQL has two files with the stem '{stem}': '{_resources[stem]}' and '{resource}'."
                );
            }
        }
    }

    /// <summary>Rendered SQL for a stem such as <c>GetOverview</c>.</summary>
    public string Load(string stem) => _rendered.GetOrAdd(stem, Render);

    /// <summary>A command with a file for this dialect runs as that text; one without runs as the routine a migration installed.</summary>
    public StoreExecutionKind Resolve(StoreCommand command) =>
        _resources.ContainsKey(command.Operation) ? StoreExecutionKind.Inline : StoreExecutionKind.Routine;

    private string Render(string stem)
    {
        if (!_resources.TryGetValue(stem, out var resource))
        {
            throw new InvalidOperationException($"The {_dialect} SQL has no file for '{stem}'.");
        }

        using var stream = _assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        // {{table}} and {{table_ref}} are substituted only for the external-outbox source catalog, whose
        // commands target a producer-owned table whose name is configurable; ledger resources never use
        // them. {{table_ref}} is the DML table reference: schema-qualified when a schema override is
        // supplied, otherwise the bare table so the database default schema (the login default on SQL
        // Server, the search_path first match on PostgreSQL) resolves it.
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
}
