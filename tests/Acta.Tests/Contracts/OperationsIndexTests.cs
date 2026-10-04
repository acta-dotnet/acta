using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Contracts;

/// <summary>
/// Generates and verifies <c>docs/reference/operations.md</c>: one row per operation folder with its facade method, its
/// HTTP routes, its CLI verb, the SQL kind per dialect, the tables its SQL writes, and its specs. Everything is read
/// from source, so the index moves with the code and needs no hand upkeep.
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
        "Definitions",
        "Workers",
        "Alerts",
        "Tenants",
        "Namespaces",
        "Tags",
        "Settings",
        "Outbox",
    ];

    [GeneratedRegex(@"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+\{\{schema\}\}\.(?<table>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenTables();

    [GeneratedRegex(@"^\s*CREATE\s+OR\s+(?:REPLACE\s+FUNCTION|ALTER\s+PROCEDURE)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RoutineHeader();

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
        var operations = Path.Combine(root, "src", "Acta.Runtime", "Operations");
        var facadeMethods = FacadeMethods(operations);
        var routes = Routes(operations, facadeMethods);
        var verbs = CliVerbs(Path.Combine(root, "src", "Acta.Runtime", "Cli", "CliCommandRunner.cs"), facadeMethods);

        var sb = new StringBuilder();
        sb.AppendLine("# Operations");
        sb.AppendLine();
        sb.AppendLine("> Generated from the source tree. Do not edit by hand. Regenerate:");
        sb.AppendLine("> `ACTA_EMIT_DOCS=1 dotnet test tests/Acta.Tests --filter OperationsIndexTests`.");
        sb.AppendLine();
        sb.AppendLine(
            "One row per operation folder, `src/Acta.Runtime/Operations/<Group>/<Operation>/`. **Facade** is the public method that runs it; an operation with none is engine-only. **HTTP** routes sit under `{mount}/v1`. The dialect columns say whether the SQL is a routine or inline. **Writes** lists the tables its SQL inserts into, updates, or deletes from. **Specs** are in `tests/Acta.Tests.Conformance/Operations/<Group>/`."
        );
        sb.AppendLine();

        foreach (var group in Directory.EnumerateDirectories(operations).Order(StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {Path.GetFileName(group)}");
            sb.AppendLine();
            sb.AppendLine("| Operation | Facade | HTTP | CLI | pg | mssql | sqlite | Writes | Specs |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var folder in Directory.EnumerateDirectories(group).Order(StringComparer.Ordinal))
            {
                var operation = Path.GetFileName(folder);
                var sql = Dialects.ToDictionary(d => d, d => Path.Combine(folder, $"{operation}.{d}.sql"));
                string[] cells =
                [
                    $"`{operation}`",
                    Cell(facadeMethods.Where(m => m.Operation == operation).Select(m => $"`I{m.Facade}.{m.Method}`")),
                    Cell(routes.Where(r => r.Operation == operation).Select(r => $"`{r.Verb} {r.Pattern}`")),
                    Cell(verbs.Where(v => v.Operation == operation).Select(v => $"`{v.Verb}`")),
                    .. Dialects.Select(d => SqlKind(sql[d])),
                    Cell(
                        Dialects
                            .SelectMany(d => Written(sql[d]))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .Select(t => $"`{t}`"),
                        ", "
                    ),
                    Cell(Specs(root, Path.GetFileName(group), operation), ", "),
                ];
                sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }

            sb.AppendLine();
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string Cell(IEnumerable<string> values, string separator = "<br>") =>
        string.Join(separator, values.Distinct(StringComparer.Ordinal)) is { Length: > 0 } joined ? joined : "·";

    private static string SqlKind(string path) =>
        !File.Exists(path) ? "·"
        : RoutineHeader().IsMatch(File.ReadAllText(path)) ? "routine"
        : "inline";

    private static IEnumerable<string> Written(string path) =>
        File.Exists(path) ? WrittenTables().Matches(File.ReadAllText(path)).Select(m => m.Groups["table"].Value.ToLowerInvariant()) : [];

    private static IEnumerable<string> Specs(string root, string group, string operation)
    {
        var folder = Path.Combine(root, "tests", "Acta.Tests.Conformance", "Operations", group);
        return Directory.Exists(folder)
            ? Directory
                .EnumerateFiles(folder, operation + "*Spec.cs")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && IsOwnSpec(name, operation))
                .Order(StringComparer.Ordinal)
                .Select(name => $"`{name}`")
            : [];
    }

    // PauseJobSpec and PauseJobFilterMatrixSpec belong to PauseJob; PauseJobsSpec does not.
    private static bool IsOwnSpec(string name, string operation) => name.Length > operation.Length && char.IsUpper(name[operation.Length]);

    // A public facade method runs an operation when its body names the operation's handler field, command, or query,
    // directly or through a helper class of the group it holds (JobLookupResolution resolves both ResolveJobId reads).
    private static List<(string Facade, string Method, string Operation)> FacadeMethods(string operations)
    {
        var names = Directory
            .EnumerateDirectories(operations)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();
        var helpers = Directory
            .EnumerateDirectories(operations)
            .SelectMany(group => Directory.EnumerateFiles(group, "*.cs"))
            .SelectMany(file => Parse(file).OfType<ClassDeclarationSyntax>())
            .GroupBy(c => c.Identifier.Text)
            .ToDictionary(g => g.Key, g => Identifiers(g.First()));
        var found = new List<(string, string, string)>();
        foreach (var facade in Facades)
        {
            var file = Directory.EnumerateFiles(operations, facade + ".cs", SearchOption.AllDirectories).SingleOrDefault();
            var type = file is null ? null : Parse(file).OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == facade);
            if (type is null)
            {
                continue;
            }

            var fields = type.ParameterList?.Parameters.ToDictionary(p => p.Identifier.Text, p => p.Type?.ToString() ?? "") ?? [];
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                var tokens = Identifiers(method);
                foreach (var field in tokens.Where(fields.ContainsKey).ToList())
                {
                    if (helpers.TryGetValue(fields[field], out var helper) && helper != tokens)
                    {
                        tokens.UnionWith(helper);
                    }
                }

                foreach (
                    var operation in names.Where(n =>
                        tokens.Contains(n + "Command") || tokens.Contains(n + "Query") || tokens.Contains(Camel(n))
                    )
                )
                {
                    found.Add((facade, method.Identifier.Text, operation));
                }
            }
        }

        return found;
    }

    private static HashSet<string> Identifiers(SyntaxNode node) =>
        node.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);

    private static List<(string Verb, string Pattern, string Operation)> Routes(
        string operations,
        List<(string Facade, string Method, string Operation)> facadeMethods
    )
    {
        var found = new List<(string, string, string)>();
        foreach (var file in Directory.EnumerateFiles(operations, "*Endpoint*.cs", SearchOption.AllDirectories))
        {
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
                    found.Add(("GET", pattern, "GetTags"));
                    found.Add(("POST", pattern, "UpsertTags"));
                    found.Add(("DELETE", pattern + "/{tagName}", "RemoveTags"));
                }
                else if (HttpVerb(name) is { } verb && MainOperation(map, facadeMethods) is { } operation)
                {
                    found.Add((verb, pattern, operation));
                }
            }
        }

        return found;
    }

    private static string? HttpVerb(string map) =>
        map switch
        {
            "MapGet" or "MapList" or "MapDetail" => "GET",
            "MapPost" or "MapCommand" => "POST",
            "MapPatch" or "MapUpdate" => "PATCH",
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
