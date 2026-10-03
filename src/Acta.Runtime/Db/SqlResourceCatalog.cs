using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Acta.Runtime;

/// <summary>
/// Catalog of one dialect's executable SQL, embedded in <c>Acta.Runtime</c> as <c>X.{dialect}.sql</c>
/// beside its operation and addressed by file stem: the stem is unique per dialect, so a caller names
/// <c>GetOverview</c> and never the folder it lives in. Ordered DDL migrations are not catalog resources
/// and stay in the provider under <c>Schema/Migrations/</c>. Lookups never fall back across dialects; a
/// missing resource is a defect and throws. Rendered text substitutes <c>{{schema}}</c>, <c>{{now}}</c>
/// (the SQLite epoch-milliseconds instant encoding; only SQLite bodies carry the token), and
/// <c>{{decode:...}}</c> tokens, cached per resource for the process lifetime.
/// </summary>
internal sealed class SqlResourceCatalog
{
    private readonly string _dialect;
    private readonly string? _schema;
    private readonly string? _table;
    private readonly Dictionary<string, (Assembly Assembly, string Name)> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rendered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoreExecutionKind> _kinds = new(StringComparer.Ordinal);

    public SqlResourceCatalog(string dialect, string? schema, string? table = null)
        : this(typeof(SqlResourceCatalog).Assembly, dialect, schema, table) { }

    /// <summary>A catalog over another assembly's <c>X.{dialect}.sql</c> resources, for test fixtures.</summary>
    internal SqlResourceCatalog(Assembly assembly, string dialect, string? schema, string? table = null)
    {
        _dialect = dialect;
        _schema = schema;
        _table = table;
        var suffix = "." + dialect + ".sql";
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(suffix, StringComparison.Ordinal)))
        {
            var name = resource[..^suffix.Length];
            Add(name[(name.LastIndexOf('.') + 1)..], assembly, resource);
        }

        // Until the move beside the slices lands, the provider assembly still embeds its SQL under Sql/.
        var provider = dialect switch
        {
            "pg" => "Acta.Postgres",
            "mssql" => "Acta.SqlServer",
            "sqlite" => "Acta.Sqlite",
            _ => null,
        };
        if (provider is null)
        {
            return;
        }

        var legacy = Assembly.Load(provider);
        foreach (var resource in legacy.GetManifestResourceNames().Where(n => n.StartsWith(provider + ".Sql.", StringComparison.Ordinal)))
        {
            var name = resource[..^".sql".Length];
            name =
                name.EndsWith(".routine", StringComparison.Ordinal) ? name[..^".routine".Length]
                : name.EndsWith(".view", StringComparison.Ordinal) ? name[..^".view".Length]
                : name;
            Add(name[(name.LastIndexOf('.') + 1)..], legacy, resource);
        }
    }

    /// <summary>Rendered SQL for a stem such as <c>GetOverview</c>.</summary>
    public string Load(string stem) => _rendered.GetOrAdd(stem, Render);

    /// <summary>A command runs as the routine its file defines, or as the file's text when it defines none.</summary>
    public StoreExecutionKind Resolve(StoreCommand command) =>
        _kinds.GetOrAdd(command.Operation, stem => DefinesRoutine(Load(stem)) ? StoreExecutionKind.Routine : StoreExecutionKind.Inline);

    /// <summary>
    /// The dialect's routine bodies, each paired with its snake_case routine name, for installation
    /// after migrations in name order. Empty for an inline-only dialect.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Routines() =>
        Installed(_resources.Keys.Where(stem => !IsView(stem) && Resolve(new StoreCommand(stem)) == StoreExecutionKind.Routine));

    /// <summary>
    /// The dialect's operator-view SELECT bodies, each paired with the snake_case view name, in name
    /// order. Schema installation owns the provider-specific CREATE/DROP wrapper; the file owns the
    /// SELECT body.
    /// </summary>
    public IEnumerable<(string Name, string Body)> Views() => Installed(_resources.Keys.Where(IsView));

    /// <summary>A file whose stem ends in <c>View</c> is an operator view body, installed as <c>x_view</c>.</summary>
    internal static bool IsView(string stem) => stem.EndsWith("View", StringComparison.Ordinal);

    /// <summary>
    /// True when the first statement, after comments, creates a routine: <c>CREATE OR REPLACE FUNCTION</c>
    /// on PostgreSQL, <c>CREATE OR ALTER PROCEDURE</c> on SQL Server. Any other file is inline SQL.
    /// </summary>
    internal static bool DefinesRoutine(string sql)
    {
        var at = 0;
        while (at < sql.Length)
        {
            if (char.IsWhiteSpace(sql[at]))
            {
                at++;
            }
            else if (string.CompareOrdinal(sql, at, "--", 0, 2) == 0)
            {
                var end = sql.IndexOf('\n', at);
                at = end < 0 ? sql.Length : end + 1;
            }
            else if (string.CompareOrdinal(sql, at, "/*", 0, 2) == 0)
            {
                var end = sql.IndexOf("*/", at + 2, StringComparison.Ordinal);
                at = end < 0 ? sql.Length : end + 2;
            }
            else
            {
                var rest = sql.AsSpan(at);
                return rest.StartsWith("CREATE OR REPLACE FUNCTION", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("CREATE OR ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private void Add(string stem, Assembly assembly, string resource)
    {
        if (!_resources.TryAdd(stem, (assembly, resource)))
        {
            throw new InvalidOperationException(
                $"The {_dialect} SQL has two files with the stem '{stem}': '{_resources[stem].Name}' and '{resource}'."
            );
        }
    }

    private IEnumerable<(string Name, string Body)> Installed(IEnumerable<string> stems) =>
        stems
            .Select(stem => (Name: JsonNamingPolicy.SnakeCaseLower.ConvertName(stem), Stem: stem))
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => (o.Name, Load(o.Stem)));

    private string Render(string stem)
    {
        if (!_resources.TryGetValue(stem, out var resource))
        {
            throw new InvalidOperationException($"The {_dialect} SQL has no file for '{stem}'.");
        }

        using var stream = resource.Assembly.GetManifestResourceStream(resource.Name)!;
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
