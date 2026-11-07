using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acta.AspNetCore;
using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// Generates and verifies <c>docs/reference/operations.md</c>: one table per group, one row per operation folder with the
/// facade method named for it, its HTTP routes with their OpenAPI operation ids, its CLI verb, its SQL kind, the tables its
/// SQL writes, and its specs, plus one row per HTTP-only operation, and a line under the group for each facade method that
/// composes several operations. Everything is read from source, so the index moves with the code and needs no hand upkeep.
/// </summary>
public sealed partial class OperationsIndexTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    // The facades a route or the CLI reaches through: IJobs directly, the rest through IActaOperations.
    private static readonly string[] Facades =
    [
        "Jobs",
        "Ledger",
        "Schedules",
        "Calendars",
        "Definitions",
        "Workers",
        "Alerts",
        "Tenants",
        "Namespaces",
        "Tags",
        "Outbox",
    ];

    [GeneratedRegex(@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+\{\{schema\}\}\.(?<table>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenTables();

    [Fact]
    public void Committed_index_matches_the_source()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var path = Path.Combine(root, "docs", "reference", "operations.md");
        var generated = Generate(root);

        if (Environment.GetEnvironmentVariable("ACTA_EMIT_DOCS") == "1")
        {
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), "docs/reference/operations.md missing: run with ACTA_EMIT_DOCS=1.");
        Assert.True(
            File.ReadAllText(path).ReplaceLineEndings("\n") == generated,
            "docs/reference/operations.md is stale. Regenerate: ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests"
        );
    }

    private static string Generate(string root)
    {
        var operations = Path.Combine(root, "src", "Acta", "Operations");
        var routeOperations = Path.Combine(root, "src", "Acta.AspNetCore", "Operations");
        var facadeMethods = FacadeMethods(operations);
        var routes = RouteMap(root);
        var httpOnly = HttpOnlyOperations(operations, routeOperations);
        var verbs = CliVerbs(Path.Combine(root, "src", "Acta", "Cli", "CliCommandRunner.cs"), facadeMethods);

        var sb = new StringBuilder();
        sb.AppendLine("# Operations");
        sb.AppendLine();
        sb.AppendLine("> Generated from the source tree. Do not edit by hand. Regenerate:");
        sb.AppendLine("> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests`.");
        sb.AppendLine();
        sb.AppendLine(
            "One row per operation folder, `src/Acta/Operations/<Group>/<Operation>/`, and one per HTTP-only operation, `src/Acta.AspNetCore/Operations/<Group>/<Operation>/`, a route that composes what the facades answer. **Facade** is the public method named for it; an operation with none is engine-only or HTTP-only, and a method that runs several operations is listed under its group. **HTTP** routes sit under `{mount}/v1`, each with its OpenAPI operation id and the permission it needs under `RequirePermissions`. **SQL** is `inline` when every dialect runs a statement, `routine` when PostgreSQL and SQL Server call an installed routine and SQLite runs the statement inline, and spelled per dialect otherwise. **Writes** lists the tables its SQL inserts into, updates, or deletes from. **Specs** are in `tests/Acta.Tests.Conformance/Operations/<Group>/`."
        );
        sb.AppendLine();

        var groups = Directory
            .EnumerateDirectories(operations)
            .Where(IsOperation)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Concat(httpOnly.Select(op => op.Group))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group}");
            sb.AppendLine();
            sb.AppendLine("| Operation | Facade | HTTP | CLI | SQL | Writes | Specs |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            var folders = Directory.Exists(Path.Combine(operations, group))
                ? Directory.EnumerateDirectories(Path.Combine(operations, group)).Where(IsOperation).ToList()
                : [];
            foreach (
                var folder in folders
                    .Concat(httpOnly.Where(op => op.Group == group).Select(op => op.Folder))
                    .OrderBy(folder => Path.GetFileName(folder), StringComparer.Ordinal)
            )
            {
                var operation = Path.GetFileName(folder);
                var sql = Dialects.ToDictionary(d => d, d => SqlFile(folder, operation, d));
                string[] cells =
                [
                    $"`{operation}`",
                    Cell(
                        facadeMethods
                            .Where(m => m.Operation == operation && OperationNames.IsNamedFor(m.Facade, m.Method, m.Operation))
                            .Select(m => $"`I{m.Facade}.{m.Method}`")
                    ),
                    Cell(
                        routes
                            .Where(r => r.Operation == operation)
                            .Select(r =>
                                $"`{r.Verb} {r.Pattern}` (`{OperationNames.OperationId(r.Operation, r.Pattern, [.. routes.Where(o => o.Operation == r.Operation).Select(o => o.Pattern)])}`, `{r.Permission}`)"
                            )
                    ),
                    Cell(verbs.Where(v => v.Operation == operation).Select(v => $"`{v.Verb}`")),
                    SqlKind(sql),
                    Cell(
                        Dialects
                            .SelectMany(d => Written(sql[d].Path))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .Select(t => $"`{t}`"),
                        ", "
                    ),
                    Cell(Specs(root, group, operation), ", "),
                ];
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }

            sb.AppendLine();
            foreach (var composite in Composites(facadeMethods, group))
            {
                sb.AppendLine(composite);
                sb.AppendLine();
            }
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }

    // A facade method named for none of the operations it runs composes them; one line per method, under its facade's group.
    private static IEnumerable<string> Composites(List<(string Facade, string Method, string Operation)> facadeMethods, string group) =>
        facadeMethods
            .Where(m => m.Facade == group)
            .GroupBy(m => m.Method)
            .Where(method => !method.Any(m => OperationNames.IsNamedFor(m.Facade, m.Method, m.Operation)))
            .OrderBy(method => method.Key, StringComparer.Ordinal)
            .Select(method =>
                $"`I{group}.{method.Key}` runs {string.Join(", ", method.Select(m => m.Operation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(op => $"`{op}`"))}."
            );

    // One word when the dialects agree on the usual split; spelled out per dialect otherwise.
    private static string SqlKind(Dictionary<string, (string Kind, string? Path)> sql) =>
        (sql["pg"].Kind, sql["mssql"].Kind, sql["sqlite"].Kind) switch
        {
            ("inline", "inline", "inline") => "inline",
            ("routine", "routine", "inline") => "routine",
            ("·", "·", "·") => "·",
            var (pg, mssql, sqlite) => $"pg {pg}, mssql {mssql}, sqlite {sqlite}",
        };

    private static string Cell(IEnumerable<string> values, string separator = "<br>") =>
        string.Join(separator, values.Distinct(StringComparer.Ordinal)) is { Length: > 0 } joined ? joined : "·";

    // X.routine.<dialect>.sql is a routine a migration installs; X.<dialect>.sql runs inline.
    private static (string Kind, string? Path) SqlFile(string folder, string operation, string dialect)
    {
        var routine = Path.Combine(folder, $"{operation}.routine.{dialect}.sql");
        var inline = Path.Combine(folder, $"{operation}.{dialect}.sql");
        return File.Exists(routine) ? ("routine", routine)
            : File.Exists(inline) ? ("inline", inline)
            : ("·", null);
    }

    private static IEnumerable<string> Written(string? path) =>
        path is null ? [] : WrittenTables().Matches(File.ReadAllText(path)).Select(m => m.Groups["table"].Value.ToLowerInvariant());

    // A spec belongs to the longest operation of its group its name starts with: GetJobIdSpec is GetJobId's, not GetJob's.
    private static IEnumerable<string> Specs(string root, string group, string operation)
    {
        var folder = Path.Combine(root, "tests", "Acta.Tests.Conformance", "Operations", group);
        var operations = Path.Combine(root, "src", "Acta", "Operations", group);
        var names = Directory.Exists(operations)
            ? Directory.EnumerateDirectories(operations).Select(Path.GetFileName).OfType<string>().ToList()
            : [operation];
        return Directory.Exists(folder)
            ? Directory
                .EnumerateFiles(folder, operation + "*Spec.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => OperationNames.SpecOwner(name, names.Append(operation)) == operation)
                .Order(StringComparer.Ordinal)
                .Select(name => $"`{name}`")
            : [];
    }

    // A public facade method runs an operation when its body names the operation's handler field, command, or query,
    // directly, through the method it calls on a helper class of the group, or through another method of the facade that
    // it or such a helper method calls (RunAndWaitAsync runs EnqueueJob through JobRunAndWait.RunAsync, which calls
    // IJobs.EnqueueAsync; WaitAsync calls JobRunAndWait.WaitAsync, which enqueues nothing).
    // The facade is the group's <Group>Operations class implementing I<Group>, and only the methods I<Group> declares
    // count: the class's engine calls are not on the public surface.
    internal static List<(string Facade, string Method, string Operation)> FacadeMethods(string operations)
    {
        var names = Directory
            .EnumerateDirectories(operations)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(IsOperation)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();
        var helpers = Directory
            .EnumerateDirectories(operations)
            .SelectMany(group =>
                Directory.EnumerateFiles(group, "*.cs", SearchOption.AllDirectories).Where(file => IsGroupLevel(group, file))
            )
            .SelectMany(file => Parse(file).OfType<ClassDeclarationSyntax>())
            .GroupBy(c => c.Identifier.Text)
            .ToDictionary(g => g.Key, g => HelperMethods(g.First()));
        var found = new List<(string, string, string)>();
        foreach (var facade in Facades)
        {
            var type = Directory
                .EnumerateFiles(Path.Combine(operations, facade), "*.cs")
                .SelectMany(file => Parse(file).OfType<ClassDeclarationSyntax>())
                .FirstOrDefault(c =>
                    c.Identifier.Text == $"{facade}Operations" && c.BaseList?.Types.Any(t => t.Type.ToString() == $"I{facade}") == true
                );
            if (type is null)
            {
                continue;
            }

            var declared = Directory
                .EnumerateFiles(Path.GetDirectoryName(operations)!, $"I{facade}.cs", SearchOption.AllDirectories)
                .SelectMany(file => Parse(file).OfType<InterfaceDeclarationSyntax>())
                .Where(i => i.Identifier.Text == $"I{facade}")
                .SelectMany(i => i.Members.OfType<MethodDeclarationSyntax>())
                .Select(m => m.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);
            var fields = type.ParameterList?.Parameters.ToDictionary(p => p.Identifier.Text, p => p.Type?.ToString() ?? "") ?? [];
            var methods = type
                .Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && declared.Contains(m.Identifier.Text))
                .Select(m => (Name: m.Identifier.Text, Tokens: WithHelpers(Identifiers(m), fields, helpers)))
                .ToList();
            foreach (var (name, tokens) in methods)
            {
                var reached = new HashSet<string>(tokens, StringComparer.Ordinal);
                foreach (var other in methods.Where(other => other.Name != name && tokens.Contains(other.Name)))
                {
                    reached.UnionWith(other.Tokens);
                }

                foreach (
                    var operation in names.Where(n =>
                        reached.Contains(n + "Command") || reached.Contains(n + "Query") || reached.Contains(Camel(n))
                    )
                )
                {
                    found.Add((facade, name, operation));
                }
            }
        }

        return found;
    }

    // The method's identifiers plus those of each group helper class it names, as a field's type or by its own name.
    private static HashSet<string> WithHelpers(
        HashSet<string> tokens,
        Dictionary<string, string> fields,
        Dictionary<string, Dictionary<string, HashSet<string>>> helpers
    )
    {
        var expanded = new HashSet<string>(tokens, StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (helpers.TryGetValue(fields.GetValueOrDefault(token, token), out var helper))
            {
                // What the helper methods named here reach; the whole class when the body names none of its methods.
                var called = tokens.Where(name => name.Length > 0 && helper.ContainsKey(name)).ToList();
                foreach (var reached in called.Count > 0 ? called.Select(name => helper[name]) : [helper[""]])
                {
                    expanded.UnionWith(reached);
                }
            }
        }

        return expanded;
    }

    // A helper class's identifiers by method: each method's own and those of the class's methods it calls, in turn. The
    // empty name holds the whole class.
    private static Dictionary<string, HashSet<string>> HelperMethods(ClassDeclarationSyntax helper)
    {
        var own = helper
            .Members.OfType<MethodDeclarationSyntax>()
            .GroupBy(method => method.Identifier.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(Identifiers).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var methods = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [""] = Identifiers(helper) };
        foreach (var (name, tokens) in own)
        {
            var reached = new HashSet<string>(tokens, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { name };
            var pending = new Queue<string>(tokens.Where(own.ContainsKey));
            while (pending.TryDequeue(out var next))
            {
                if (seen.Add(next))
                {
                    reached.UnionWith(own[next]);
                    foreach (var called in own[next].Where(own.ContainsKey))
                    {
                        pending.Enqueue(called);
                    }
                }
            }

            methods[name] = reached;
        }

        return methods;
    }

    private static HashSet<string> Identifiers(SyntaxNode node) =>
        node.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);

    // A Shared/ folder holds what several operations share; it is neither an operation nor, under Operations/, a group.
    private static bool IsOperation(string folder) => Path.GetFileName(folder) != "Shared";

    // A file at the group's root or in its Shared/ folder, where the helpers a facade method may call live.
    private static bool IsGroupLevel(string group, string file) =>
        Path.GetDirectoryName(file) is { } dir && (dir == group || dir == Path.Combine(group, "Shared"));

    // A route folder whose endpoint names no operation of the SDK: the route composes what the facades answer.
    private static List<(string Group, string Folder)> HttpOnlyOperations(string operations, string routeOperations)
    {
        var sdk = Directory
            .EnumerateDirectories(operations)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(IsOperation)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        return
        [
            .. Directory
                .EnumerateDirectories(routeOperations)
                .SelectMany(group =>
                    Directory.EnumerateDirectories(group).Select(folder => (Group: Path.GetFileName(group), Folder: folder))
                )
                .Where(op =>
                    !sdk.Contains(Path.GetFileName(op.Folder))
                    && File.Exists(Path.Combine(op.Folder, Path.GetFileName(op.Folder) + "Endpoint.cs"))
                ),
        ];
    }

    /// <summary>
    /// Every route the HTTP surface maps, as <c>VERB /pattern</c> below the version segment, with the operation it serves and
    /// the permission its <see cref="ActaOperationAttribute"/> names.
    /// </summary>
    internal static List<(string Verb, string Pattern, string Operation, string Permission)> RouteMap(string root) =>
        [
            .. Routes(
                    Path.Combine(root, "src", "Acta.AspNetCore", "Operations"),
                    FacadeMethods(Path.Combine(root, "src", "Acta", "Operations"))
                )
                .Distinct(),
        ];

    [Fact]
    public void Each_routes_permission_is_the_one_its_OpenAPI_operation_carries()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var paths = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "reference", "openapi.json")))!["paths"]!;
        var routes = RouteMap(root);

        Assert.NotEmpty(routes);
        Assert.Empty(
            routes
                .Where(r =>
                    paths[ActaEndpointRouteBuilderExtensions.ApiVersionSegment + r.Pattern]
                        ?[r.Verb.ToLowerInvariant()]?["x-acta-permission"]?.GetValue<string>() != r.Permission
                )
                .Select(r => $"{r.Verb} {r.Pattern}: {r.Permission}")
        );
    }

    // A route in its own folder belongs to that folder's operation; a route in a group's table, to the facade call it makes.
    private static List<(string Verb, string Pattern, string Operation, string Permission)> Routes(
        string operations,
        List<(string Facade, string Method, string Operation)> facadeMethods
    )
    {
        var found = new List<(string, string, string, string)>();
        foreach (
            var file in Directory.EnumerateFiles(operations, "*Endpoint*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        )
        {
            var own = Path.GetRelativePath(operations, file).Split(Path.DirectorySeparatorChar) is [_, var folder, _] ? folder : null;
            foreach (var map in Parse(file).OfType<InvocationExpressionSyntax>())
            {
                if (
                    map.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name }
                    || !name.StartsWith("Map", StringComparison.Ordinal)
                )
                {
                    continue;
                }

                var pattern = map
                    .ArgumentList.Arguments.Select(a => a.Expression)
                    .OfType<LiteralExpressionSyntax>()
                    .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression))
                    ?.Token.ValueText;
                if (pattern is null)
                {
                    continue;
                }

                if (name == "MapTags")
                {
                    // MapTags(controls, pattern, entity, writePermission, ...): the list reads, the writes need the owner's permission.
                    var write = PermissionValue(map.ArgumentList.Arguments[3].Expression, file);
                    found.Add(("GET", pattern, "ListTags", ActaPermissions.Read));
                    found.Add(("PUT", pattern + "/{tagName}", "UpsertTag", write));
                    found.Add(("DELETE", pattern + "/{tagName}", "RemoveTag", write));
                }
                else if (HttpVerb(name) is { } verb && (own ?? MainOperation(map, facadeMethods)) is { } operation)
                {
                    found.Add((verb, pattern, operation, Permission(map, file)));
                }
            }
        }

        return found;
    }

    // A route names its ActaOperationAttribute either as a route helper's argument, new("id", ActaPermissions.X), or in
    // the chain after the Map call, .WithMetadata(new ActaOperationAttribute("id", ActaPermissions.X)).
    private static string Permission(InvocationExpressionSyntax map, string file)
    {
        SyntaxNode chain = map;
        while (chain.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax outer })
        {
            chain = outer;
        }

        var attribute =
            map.ArgumentList.Arguments.Select(a => a.Expression)
                .OfType<ImplicitObjectCreationExpressionSyntax>()
                .FirstOrDefault()
                ?.ArgumentList
            ?? chain
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .FirstOrDefault(o => o.Type.ToString().EndsWith(nameof(ActaOperationAttribute), StringComparison.Ordinal))
                ?.ArgumentList;
        return attribute is { Arguments: [_, var permission, ..] }
            ? PermissionValue(permission.Expression, file)
            : throw new InvalidOperationException($"{file}: a route maps no ActaOperationAttribute: {map}");
    }

    // ActaPermissions.X read from the constant itself, so the index prints the wire name.
    private static string PermissionValue(ExpressionSyntax expression, string file) =>
        expression
            is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: nameof(ActaPermissions) },
                Name.Identifier.Text: var name
            }
        && typeof(ActaPermissions).GetField(name)?.GetValue(null) is string value
            ? value
            : throw new InvalidOperationException($"{file}: a permission is not an ActaPermissions constant: {expression}");

    private static string? HttpVerb(string map) =>
        map switch
        {
            "MapGet" or "MapList" or "MapDetail" => "GET",
            "MapPost" or "MapCommand" => "POST",
            "MapPut" or "MapUpdate" => "PUT",
            "MapDelete" => "DELETE",
            _ => null,
        };

    // The CLI verb whose switch arm, or the runner method the arm calls, runs the operation.
    private static List<(string Verb, string Operation)> CliVerbs(
        string runner,
        List<(string Facade, string Method, string Operation)> facadeMethods
    )
    {
        var nodes = Parse(runner).ToList();
        var methods = nodes.OfType<MethodDeclarationSyntax>().GroupBy(m => m.Identifier.Text).ToDictionary(g => g.Key, g => g.First());
        var found = new List<(string, string)>();
        foreach (var arm in nodes.OfType<SwitchExpressionArmSyntax>())
        {
            if (
                arm.Pattern
                is not ConstantPatternSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "CliVerb" } } member
                }
            )
            {
                continue;
            }

            var operation =
                MainOperation(arm.Expression, facadeMethods)
                ?? arm.Expression.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>()
                    .Select(call =>
                        call.Expression is IdentifierNameSyntax callee && methods.TryGetValue(callee.Identifier.Text, out var body)
                            ? SoleOperation(body, facadeMethods)
                            : null
                    )
                    .LastOrDefault(op => op is not null);
            if (operation is not null)
            {
                found.Add((member.Name.Identifier.Text.ToLowerInvariant(), operation));
            }
        }

        return found;
    }

    // A runner helper names the verb's operation only when it runs one; debug, which runs several, maps to none.
    private static string? SoleOperation(SyntaxNode scope, List<(string Facade, string Method, string Operation)> facadeMethods) =>
        scope
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(call => Operation(call, facadeMethods))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList()
            is [var sole]
            ? sole
            : null;

    // The last facade call not nested in another one's arguments: a lookup that feeds the real call is not the route's
    // operation.
    private static string? MainOperation(SyntaxNode scope, List<(string Facade, string Method, string Operation)> facadeMethods)
    {
        var calls = scope
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(call => (Call: call, Operation: Operation(call, facadeMethods)))
            .Where(c => c.Operation is not null)
            .ToList();
        return calls
            .LastOrDefault(c => !calls.Any(outer => outer.Call != c.Call && outer.Call.ArgumentList.Span.Contains(c.Call.Span)))
            .Operation;
    }

    private static string? Operation(InvocationExpressionSyntax call, List<(string Facade, string Method, string Operation)> facadeMethods)
    {
        if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var method, Expression: var receiver })
        {
            return null;
        }

        var facade = receiver switch
        {
            MemberAccessExpressionSyntax inner => inner.Name.Identifier.Text,
            IdentifierNameSyntax plain => plain.Identifier.Text,
            _ => null,
        };
        var matches = facadeMethods
            .Where(m => m.Method == method && string.Equals(m.Facade, facade?.TrimStart('_'), StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Operation)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static IEnumerable<SyntaxNode> Parse(string file) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes();

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
