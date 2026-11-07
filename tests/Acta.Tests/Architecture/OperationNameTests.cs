using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Acta.AspNetCore;
using Acta.Tests.Conformance.Testing;
using Acta.Tests.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds an operation to one name from the route down to the SQL, as <c>CONTRIBUTING.md</c> (Naming conventions) states
/// it. The folder gives the name. <see cref="OperationLayoutTests"/> and <see cref="HandlerResponseTests"/> hold the
/// files and types in the folder to it; here the layers around the folder are held to it: the method that runs the
/// handler, the facade method, its SQL files and the strings that load them and scope its cursor, the route folder, the
/// route's OpenAPI id, the spec, and the verb.
/// </summary>
public sealed class OperationNameTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    // The calls that load SQL by its stem: the session's reads, the catalog's load, and a write's StoreCommand.
    private static readonly HashSet<string> Loads = ["QueryAsync", "QuerySingleAsync", "QueryListAsync", "QueryPageAsync", "Load"];

    // The verbs an operation may start with. A new verb is a new word in the vocabulary, so it is added only on purpose.
    private static readonly HashSet<string> Verbs =
    [
        // Reads: one thing, items, a number, and the two that compute an answer and store nothing.
        .. "Get List Count Explain Preview".Split(' '),
        // Everything else changes state.
        .. "Access Acknowledge Acquire Admit Archive Arm Cancel Claim Complete Consume Discard Drop Enqueue Ensure".Split(' '),
        .. "Extend Mark Park Pause Plan Publish Purge Quarantine Raise Reclaim Record Register Release Remove".Split(' '),
        .. "Replace Reprioritize Requeue Reschedule Reserve Reset Resolve Restart Resume Retire Save Start Stop".Split(' '),
        .. "Suspend Trigger Update Upsert Wait".Split(' '),
    ];

    // A verb that names one group's act and nothing else: resolving closes an alert, and a lookup is a Get.
    private static readonly Dictionary<string, string> VerbGroup = new(StringComparer.Ordinal) { ["Resolve"] = "Alerts" };

    // A method that runs an operation in a second form, beside the one named for it; each entry says what differs.
    private static readonly HashSet<(string Method, string Operation)> SecondForms =
    [
        // The enqueue under its parent's fence, which only a running handler holds.
        ("JobsOperations.EnqueueChildAsync", "EnqueueJob"),
        // The installer lock under the database's outbox key instead of a schema's.
        ("SchemaOperations.AcquireOutboxLockAsync", "AcquireSchemaLock"),
    ];

    // A read that takes another operation's statement; each entry says why it has no statement of its own.
    private static readonly HashSet<(string Operation, string Names)> BorrowedStatements =
    [
        // The id of one definition is the list's statement taken for the one row its two keys name.
        ("GetDefinitionId", "ListDefinitions"),
    ];

    private static readonly string Operations = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta", "Operations");

    private static readonly HashSet<string> Names =
    [
        .. Directory.EnumerateDirectories(Operations).SelectMany(Folders).Select(folder => Path.GetFileName(folder)!),
    ];

    /// <summary>
    /// An operation, and a route folder in <c>Acta.AspNetCore</c>, starts with a verb from the closed list, so no second
    /// word for one act comes in unnoticed, and a verb bound to one group stays in it. Every listed verb is in use.
    /// </summary>
    [Fact]
    public void An_operation_starts_with_a_listed_verb()
    {
        var routes = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.AspNetCore", "Operations");
        var named = Directory
            .EnumerateDirectories(Operations)
            .SelectMany(Folders)
            .Concat(Directory.EnumerateDirectories(routes).SelectMany(Directory.EnumerateDirectories))
            .Select(folder => (Group: Path.GetFileName(Path.GetDirectoryName(folder))!, Name: Path.GetFileName(folder)!))
            .Select(operation => (operation.Group, operation.Name, Verb: OperationNames.Verb(operation.Name)))
            .ToList();
        Assert.True(named.Count > 150, $"only {named.Count} operations and route folders; the walk is broken.");

        var unlisted = named
            .Where(operation => !Verbs.Contains(operation.Verb))
            .Select(operation => ($"{operation.Group}/{operation.Name}", (string?)$"{operation.Verb} is not a listed verb"));
        var misplaced = named
            .Where(operation => VerbGroup.TryGetValue(operation.Verb, out var group) && group != operation.Group)
            .Select(operation =>
                ($"{operation.Group}/{operation.Name}", (string?)$"{operation.Verb} names an act of {VerbGroup[operation.Verb]} only")
            );
        var unused = Verbs.Except(named.Select(operation => operation.Verb)).Select(verb => (verb, (string?)"starts no operation"));

        ArchitectureAssert.NoViolations(
            unlisted.Concat(misplaced).Concat(unused),
            "Name the operation with a listed verb (a read is a Get or a List, a lookup a Get, taking rows away a Remove), or add the verb on purpose."
        );
    }

    /// <summary>
    /// A method of <c>&lt;Group&gt;Operations</c> that runs one handler is that operation: <c>&lt;Operation&gt;Async</c> when
    /// the engine calls it, and the name without the group's noun when it is public, as a facade method is. The field
    /// or parameter holding the handler is the operation in camelCase, without the noun in a nested class, whose methods
    /// all drop it.
    /// </summary>
    [Fact]
    public void A_method_that_runs_one_handler_is_named_for_its_operation()
    {
        var violations = new List<(string, string?)>();
        var methods = 0;
        foreach (var group in Directory.EnumerateDirectories(Operations).Where(group => Path.GetFileName(group) != "Shared"))
        {
            var name = Path.GetFileName(group);
            var file = Path.Combine(group, $"{name}Operations.cs");
            foreach (var type in Parse(file).OfType<ClassDeclarationSyntax>())
            {
                var handlers = Handlers(type);
                foreach (var (field, operation) in handlers)
                {
                    var expected = Camel(type.Parent is ClassDeclarationSyntax ? OperationNames.FacadeMethod(name, operation) : operation);
                    if (field.TrimStart('_') != expected)
                    {
                        violations.Add(($"{type.Identifier.Text}: {field}", $"holds {operation}Handler; name it {expected}"));
                    }
                }
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var run = Run(method, handlers);
                    if (run.Count != 1)
                    {
                        continue;
                    }

                    methods++;
                    var isPublic = method.Modifiers.Any(SyntaxKind.PublicKeyword);
                    var expected = isPublic ? OperationNames.FacadeMethod(name, run[0]) : run[0];
                    if (
                        method.Identifier.Text != expected
                        && method.Identifier.Text != expected + "Async"
                        && !SecondForms.Contains(($"{type.Identifier.Text}.{method.Identifier.Text}", run[0]))
                    )
                    {
                        violations.Add(
                            ($"{type.Identifier.Text}.{method.Identifier.Text}", $"runs {run[0]} alone; name it {expected}Async")
                        );
                    }
                }
            }
        }

        Assert.True(methods > 100, $"only {methods} methods run one handler; the walk is broken.");
        ArchitectureAssert.NoViolations(
            violations,
            "Name the method after the one operation it runs: <Operation>Async for the engine, without the group's noun when public."
        );
    }

    /// <summary>
    /// A facade method is its operation's name without the group's noun, plus <c>Async</c> on a task: <c>PauseJob</c> is
    /// <c>IJobs.PauseAsync</c>. Every operation a facade runs has a method named so; a method that runs several, such as
    /// <c>IJobs.WaitAsync</c>, sits beside them.
    /// </summary>
    [Fact]
    public void Every_operation_a_facade_runs_has_a_method_named_without_the_group_noun()
    {
        var map = OperationsIndexTests.FacadeMethods(Operations);
        Assert.True(map.Count > 50, $"the facade map found only {map.Count} methods; the walk is broken.");

        var violations = map.GroupBy(entry => (entry.Facade, entry.Operation))
            .Where(group => !group.Any(entry => OperationNames.IsNamedFor(entry.Facade, entry.Method, entry.Operation)))
            .Select(group =>
                (
                    $"I{group.Key.Facade}: {group.Key.Operation}",
                    (string?)
                        $"no method {OperationNames.FacadeMethod(group.Key.Facade, group.Key.Operation)}Async; it runs through {string.Join(", ", group.Select(e => e.Method).Order(StringComparer.Ordinal))}"
                )
            );

        ArchitectureAssert.NoViolations(
            violations,
            "Name the facade method after its operation without the group's noun; when the verbs disagree, rename the operation to the public verb."
        );
    }

    /// <summary>
    /// The string that loads an operation's SQL, and the one that scopes its cursor, is the operation's name, so the
    /// installed routine is its snake case. No operation's code spells another operation's name as a string.
    /// </summary>
    [Fact]
    public void An_operation_spells_no_other_operations_name()
    {
        var violations = Directory
            .EnumerateDirectories(Operations)
            .SelectMany(Folders)
            .SelectMany(folder =>
                Directory
                    .EnumerateFiles(folder, "*.cs")
                    .SelectMany(file => Parse(file).OfType<LiteralExpressionSyntax>())
                    .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
                    .Select(literal => literal.Token.ValueText)
                    .Where(text => Names.Contains(text) && text != Path.GetFileName(folder))
                    .Where(text => !BorrowedStatements.Contains((Path.GetFileName(folder)!, text)))
                    .Select(text => ($"{Path.GetFileName(folder)}: \"{text}\"", (string?)null))
            );

        ArchitectureAssert.NoViolations(violations, "Load the operation's own SQL under its own name, or list why it borrows a statement.");
    }

    /// <summary>
    /// Every SQL file in an operation's folder is the operation's own statement, <c>&lt;Operation&gt;.&lt;dialect&gt;.sql</c>
    /// or <c>&lt;Operation&gt;.routine.&lt;dialect&gt;.sql</c>, so the folder holds one statement and a second statement is a
    /// second operation.
    /// </summary>
    [Fact]
    public void An_operation_folder_holds_only_its_own_sql()
    {
        var files = Directory
            .EnumerateDirectories(Operations)
            .SelectMany(Folders)
            .SelectMany(folder =>
                Directory
                    .EnumerateFiles(folder, "*.sql")
                    .Select(file => (Operation: Path.GetFileName(folder)!, File: Path.GetFileName(file)))
            )
            .ToList();
        Assert.True(files.Count > 300, $"only {files.Count} SQL files; the walk is broken.");

        var violations = files
            .Where(sql =>
                !Dialects.Any(dialect =>
                    sql.File == $"{sql.Operation}.{dialect}.sql" || sql.File == $"{sql.Operation}.routine.{dialect}.sql"
                )
            )
            .Select(sql => ($"{sql.Operation}: {sql.File}", (string?)null));

        ArchitectureAssert.NoViolations(
            violations,
            "Name the file <Operation>.<dialect>.sql or <Operation>.routine.<dialect>.sql; a second statement is a second operation."
        );
    }

    /// <summary>
    /// The stem an operation's code loads, through the session's reads, the catalog's load, or a write's
    /// <c>StoreCommand</c>, is the operation's own name, spelled as a literal so it can be read here. A read that borrows
    /// another operation's statement is listed with its reason.
    /// </summary>
    [Fact]
    public void An_operation_loads_its_own_statement()
    {
        var loads = Directory
            .EnumerateDirectories(Operations)
            .SelectMany(Folders)
            .SelectMany(folder =>
                Directory
                    .EnumerateFiles(folder, "*.cs")
                    .SelectMany(file => Stems(Parse(file)))
                    .Select(stem => (Operation: Path.GetFileName(folder)!, Stem: stem))
            )
            .ToList();
        Assert.True(loads.Count > 100, $"only {loads.Count} SQL loads; the walk is broken.");

        var violations = loads
            .Where(load => load.Stem != load.Operation && !BorrowedStatements.Contains((load.Operation, load.Stem ?? "")))
            .Select(load =>
                ($"{load.Operation}: {(load.Stem is { } stem ? $"\"{stem}\"" : "a stem that is not a string literal")}", (string?)null)
            );

        ArchitectureAssert.NoViolations(violations, "Load the operation's own statement under its own name, or list why it borrows one.");
    }

    /// <summary>
    /// A route folder in <c>Acta.AspNetCore</c> is named for the operation it serves, with the owner before the subject
    /// when one operation serves several owners (<c>UpsertTenantTag</c>). A folder that names none composes several
    /// operations and holds its own endpoint.
    /// </summary>
    [Fact]
    public void A_route_folder_is_named_for_its_operation()
    {
        var routes = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.AspNetCore", "Operations");
        var folders = Directory.EnumerateDirectories(routes).SelectMany(Directory.EnumerateDirectories).ToList();
        Assert.True(folders.Count > 20, $"only {folders.Count} route folders; the walk is broken.");

        var violations = folders
            .Select(folder => (Folder: folder, Name: Path.GetFileName(folder)!))
            .Where(route => OperationNames.RouteOwner(route.Name, Names) is null)
            .Where(route => !File.Exists(Path.Combine(route.Folder, route.Name + "Endpoint.cs")))
            .Select(route => ($"{Path.GetFileName(Path.GetDirectoryName(route.Folder))}/{route.Name}", (string?)null));

        ArchitectureAssert.NoViolations(
            violations,
            "Name the route folder after the operation it serves, or give a route that composes several its own <Name>Endpoint."
        );
    }

    /// <summary>
    /// A route's OpenAPI operation id is the name of the operation the operations index attributes it to, in camelCase
    /// (<c>pauseJob</c>): the owner goes before the subject when one operation serves routes of several owners
    /// (<c>listJobTags</c>), the route with no owner keeps the plain name (<c>listEvents</c>), and of two routes of one
    /// owner the second adds its distinguishing path word after the subject (<c>getJobByDeduplicationKey</c>). No two
    /// routes share one.
    /// </summary>
    [Fact]
    public void A_route_id_is_its_operations_name()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var routes = OperationsIndexTests.RouteMap(root);
        var paths = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "reference", "openapi.json")))!["paths"]!;
        var ids = routes
            .Select(route =>
                (
                    Route: $"{route.Verb} {route.Pattern}",
                    Id: paths[ActaEndpointRouteBuilderExtensions.ApiVersionSegment + route.Pattern]
                        ?[route.Verb.ToLowerInvariant()]?["operationId"]?.GetValue<string>(),
                    Expected: OperationNames.OperationId(
                        route.Operation,
                        route.Pattern,
                        [.. routes.Where(other => other.Operation == route.Operation).Select(other => other.Pattern)]
                    )
                )
            )
            .ToList();
        Assert.True(ids.Count > 60, $"only {ids.Count} routes; the walk is broken.");

        var misnamed = ids.Where(route => route.Id != route.Expected)
            .Select(route => (route.Route, (string?)$"has the id '{route.Id}'; name it {route.Expected}"));
        var shared = ids.GroupBy(route => route.Expected, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => ($"'{group.Key}'", (string?)$"names {string.Join(", ", group.Select(route => route.Route))}"));

        ArchitectureAssert.NoViolations(
            misnamed.Concat(shared),
            "Give the route its operation's name in camelCase: the owner before the subject when it serves several owners, and a literal path word after the subject that tells two routes of one owner apart."
        );
    }

    /// <summary>
    /// A spec in a group's conformance folder starts with the operation of the group it proves
    /// (<c>GetJobIdByRefSpec</c>), or with the group's noun when it proves a rule across the group's operations
    /// (<c>JobIdentifierShapeSpec</c>), so its name says where to read the code it holds to its contract.
    /// </summary>
    [Fact]
    public void A_spec_starts_with_an_operation_of_its_group_or_the_groups_noun()
    {
        var root = Path.Combine(IntegrationConfig.FindRepoRoot(), "tests", "Acta.Tests.Conformance", "Operations");
        var specs = Directory
            .EnumerateDirectories(root)
            .SelectMany(group =>
                Directory
                    .EnumerateFiles(group, "*Spec.cs")
                    .Select(file => (Group: Path.GetFileName(group)!, Spec: Path.GetFileNameWithoutExtension(file)!))
            )
            .ToList();
        Assert.True(specs.Count > 100, $"only {specs.Count} specs; the walk is broken.");

        var violations = specs
            .Where(spec =>
                OperationNames.SpecOwner(spec.Spec, Folders(Path.Combine(Operations, spec.Group)).Select(Path.GetFileName).OfType<string>())
                    is null
                && OperationNames.SpecNoun(spec.Spec, spec.Group) is null
            )
            .Select(spec => ($"{spec.Group}/{spec.Spec}", (string?)null));

        ArchitectureAssert.NoViolations(
            violations,
            "Start the spec's name with the operation it proves, or with its group's noun when it proves a rule across the group."
        );
    }

    /// <summary>
    /// The verb says the shape: a <c>Get</c> answers one thing, so its response is never a collection or a wrapper
    /// around one, and a <c>List</c> answers items, a page when a caller sees it and its rows in the engine.
    /// </summary>
    [Fact]
    public void A_get_answers_one_thing_and_a_list_answers_items()
    {
        var handlers = ShippedCode
            .Types.Where(type => type.Name.EndsWith("Handler", StringComparison.Ordinal) && Names.Contains(type.Name[..^"Handler".Length]))
            .Select(type =>
                (
                    Operation: type.Name[..^"Handler".Length],
                    Returns: type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(method => method.Name is "HandleAsync" or "Handle")
                        .Select(method => Returned(method.ReturnType))
                        .FirstOrDefault()
                )
            )
            .ToList();
        Assert.True(handlers.Count > 100, $"only {handlers.Count} handlers; the walk is broken.");

        var gets = handlers
            .Where(handler => handler.Operation.StartsWith("Get", StringComparison.Ordinal) && Items(handler.Returns))
            .Select(handler => (handler.Operation, (string?)"answers items; name it List"));
        var lists = handlers
            .Where(handler => handler.Operation.StartsWith("List", StringComparison.Ordinal) && !Items(handler.Returns))
            .Select(handler => (handler.Operation, (string?)"answers no items; a List returns a page or its rows"));

        ArchitectureAssert.NoViolations(gets.Concat(lists), "Name a read for what it answers: Get for one thing, List for items.");
    }

    // Items: a collection, a page, or a record whose only member is a collection.
    private static bool Items(Type? type) =>
        type is not null
        && (
            Collection(type)
            || type.GetInterfaces()
                .Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPagedResponse<>))
            || type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.Name != "EqualityContract")
                .ToList()
                is [var only]
                && Collection(only.PropertyType)
        );

    private static bool Collection(Type type) =>
        type != typeof(string) && type != typeof(byte[]) && typeof(IEnumerable).IsAssignableFrom(type);

    private static Type? Returned(Type returnType) =>
        returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask) ? null
        : returnType.IsGenericType
        && returnType.GetGenericTypeDefinition() is var open
        && (open == typeof(Task<>) || open == typeof(ValueTask<>))
            ? returnType.GetGenericArguments()[0]
        : returnType;

    // The handlers a class holds, by the name it holds them under: a primary-constructor parameter or a field.
    private static Dictionary<string, string> Handlers(ClassDeclarationSyntax type)
    {
        var held =
            type.ParameterList?.Parameters.Select(parameter => (Name: parameter.Identifier.Text, Type: parameter.Type?.ToString())) ?? [];
        var fields = type
            .Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field =>
                field.Declaration.Variables.Select(variable =>
                    (Name: variable.Identifier.Text, Type: (string?)field.Declaration.Type.ToString())
                )
            );
        return held.Concat(fields)
            .Where(entry => entry.Type is not null && entry.Type.EndsWith("Handler", StringComparison.Ordinal))
            .Where(entry => Names.Contains(entry.Type![..^"Handler".Length]))
            .ToDictionary(entry => entry.Name, entry => entry.Type![..^"Handler".Length], StringComparer.Ordinal);
    }

    // The operations whose handler the method calls, each once.
    private static List<string> Run(MethodDeclarationSyntax method, Dictionary<string, string> handlers) =>
        [
            .. method
                .DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(access => access.Name.Identifier.Text is "HandleAsync" or "Handle")
                .Select(access => access.Expression)
                .OfType<IdentifierNameSyntax>()
                .Select(holder => handlers.GetValueOrDefault(holder.Identifier.Text))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ];

    // The stem each SQL load in the nodes names: its first argument's literal, or null when that argument is no literal.
    private static IEnumerable<string?> Stems(IEnumerable<SyntaxNode> nodes) =>
        nodes
            .Select(node =>
                node switch
                {
                    InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call
                        when Loads.Contains(access.Name.Identifier.Text) => call.ArgumentList.Arguments.FirstOrDefault()?.Expression,
                    ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.Text: "StoreCommand" } } create => create
                        .ArgumentList?.Arguments.FirstOrDefault()
                        ?.Expression,
                    _ => null,
                }
            )
            .OfType<ExpressionSyntax>()
            .Select(stem =>
                stem is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
                    ? literal.Token.ValueText
                    : null
            );

    private static IEnumerable<string> Folders(string group) =>
        Path.GetFileName(group) == "Shared"
            ? []
            : Directory.EnumerateDirectories(group).Where(folder => Path.GetFileName(folder) != "Shared");

    private static IEnumerable<SyntaxNode> Parse(string path) =>
        File.Exists(path) ? CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes() : [];

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
