using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds operations to "every operation has its own input and its own response" in <c>docs/internals/naming-conventions.md</c>:
/// <c>XHandler.HandleAsync</c> takes <c>XCommand</c> or <c>XQuery</c>, a command returns <c>XResponse</c> or nothing, a list
/// returns its own page, a Get its own <c>GetXResponse</c>, and a row, command, query, response, or response element is
/// declared in its operation's folder under the operation's name, a row named by no other operation; any other type in
/// that folder, such as a response's part or the enum its SQL answers in, starts with the operation's name. What predates
/// a rule is listed in its ratchet, <c>handler-response</c> or <c>slice-members</c>.
/// </summary>
public sealed class HandlerResponseTests
{
    private const string OperationRoot = "src/Acta/Operations/";

    // What an operation owns outright; of these, only a row is also kept from every other operation.
    private static readonly string[] SliceRoles = ["Row", "Command", "Query", "Response", "Item"];

    [Fact]
    public void A_handler_takes_its_operations_input_and_returns_its_response()
    {
        var handlers = ShippedCode
            .Types.Where(type => type.Name.EndsWith("Handler", StringComparison.Ordinal))
            .SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name is "HandleAsync" or "Handle")
            )
            .Select(method =>
                (Handler: method.DeclaringType!, Input: method.GetParameters()[0].ParameterType, Returns: Returned(method.ReturnType))
            )
            .ToList();

        // An operation with no input takes only the token.
        var inputs = handlers
            .Where(handler => handler.Input != typeof(CancellationToken) && !OwnInput(handler.Handler, handler.Input))
            .Select(handler =>
                ($"{ShippedCode.Key(handler.Handler)} (takes {Describe(handler.Input)})", (string?)"HandleAsync takes another input")
            );
        // A command that answers nothing returns a plain task.
        var responses = handlers
            .Where(handler => handler.Input.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(handler => handler.Returns is not null && handler.Returns.Name != handler.Input.Name[..^"Command".Length] + "Response")
            .Select(handler => (ShippedCode.Key(handler.Handler), (string?)$"{handler.Input.Name} returns {Describe(handler.Returns)}"));
        // A list returns its own page; a public one pages through IPagedResponse.
        var lists = handlers
            .Where(handler =>
                handler.Input.Name.StartsWith("List", StringComparison.Ordinal)
                && handler.Input.Name.EndsWith("Query", StringComparison.Ordinal)
            )
            .Where(handler =>
                handler.Returns?.Name != handler.Input.Name[..^"Query".Length] + "Response"
                || (handler.Input.IsPublic && !Paged(handler.Returns))
            )
            .Select(handler => (ShippedCode.Key(handler.Handler), (string?)$"{handler.Input.Name} returns {Describe(handler.Returns)}"));
        // A Get returns its own response, with or without a query; null stays its "nothing found".
        var gets = handlers
            .Select(handler => (handler.Handler, handler.Returns, Operation: handler.Handler.Name[..^"Handler".Length]))
            .Where(handler => handler.Operation.StartsWith("Get", StringComparison.Ordinal))
            .Where(handler => handler.Returns?.Name != handler.Operation + "Response")
            .Select(handler => (ShippedCode.Key(handler.Handler), (string?)$"{handler.Operation} returns {Describe(handler.Returns)}"));

        Ratchet.Check(
            "handler-response",
            inputs.Concat(responses).Concat(lists).Concat(gets),
            "Take the operation's own XCommand or XQuery in HandleAsync, return its own XResponse, or nothing, from a command, "
                + "its own ListXResponse : IPagedResponse<ListXItem> from a list, and its own GetXResponse from a Get."
        );
    }

    private static bool Paged(Type? response) =>
        response is not null
        && response
            .GetInterfaces()
            .Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IPagedResponse<>));

    private static bool OwnInput(Type handler, Type input)
    {
        var operation = handler.Name[..^"Handler".Length];
        return input.Name == operation + "Command" || input.Name == operation + "Query";
    }

    [Fact]
    public void A_row_belongs_to_one_slice()
    {
        var files = ShippedCode
            .SourceFiles("*.cs")
            .Where(file => file.Relative.StartsWith(OperationRoot, StringComparison.Ordinal))
            .Select(file =>
                (
                    Slice: Slice(file.Relative[OperationRoot.Length..]),
                    Root: CSharpSyntaxTree.ParseText(File.ReadAllText(file.Path)).GetRoot()
                )
            )
            .ToList();

        // The operation folders that name each identifier, crefs included; a group-level file may name any row.
        var users = files
            .Where(file => file.Slice is not null)
            .SelectMany(file =>
                file.Root.DescendantNodes(descendIntoTrivia: true)
                    .OfType<SimpleNameSyntax>()
                    .Select(name => (Name: name.Identifier.Text, Slice: file.Slice!))
            )
            .ToLookup(use => use.Name, use => use.Slice, StringComparer.Ordinal);

        // An interface is the contract several responses share, not a response.
        var types = files
            .SelectMany(file =>
                file.Root.DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .Where(type => type is not InterfaceDeclarationSyntax)
                    .Select(type => (Name: type.Identifier.Text, file.Slice))
            )
            .ToList();
        var violations = types.SelectMany(type => SliceViolations(type.Name, type.Slice, users));

        Ratchet.Check(
            "slice-members",
            violations,
            "Declare the type in its operation's folder, named for the operation, and keep a row to that operation."
        );
    }

    // "Group/Operation" for a file in an operation folder, null for a group-level file; the path is below Operations/.
    private static string? Slice(string path) => path.Split('/') is [var group, var operation, _] ? $"{group}/{operation}" : null;

    private static IEnumerable<(string Key, string? Detail)> SliceViolations(string name, string? slice, ILookup<string, string> users)
    {
        var role = SliceRoles.FirstOrDefault(role => name.EndsWith(role, StringComparison.Ordinal));
        if (slice is null)
        {
            if (role is not null)
            {
                yield return ($"{name} (outside a slice)", "declared in a group-level file");
            }
            yield break;
        }

        // A row, and every type without a role (a response's part, an enum, a handler, an option), is named for its operation.
        var operation = slice[(slice.IndexOf('/') + 1)..];
        if (role is null or "Row" ? !name.StartsWith(operation, StringComparison.Ordinal) : name != operation + role)
        {
            yield return ($"{name} (named for another operation)", $"declared in {slice}");
        }
        if (role == "Row")
        {
            foreach (var other in users[name].Where(user => user != slice).Distinct().Order(StringComparer.Ordinal))
            {
                yield return ($"{name} (shared)", $"also named in {other}");
            }
        }
    }

    // What the caller gets: the T of Task<T> or ValueTask<T>, nothing for the plain ones or void, and what a synchronous
    // Handle returns.
    private static Type? Returned(Type returnType) =>
        returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask) ? null
        : returnType.IsGenericType
        && returnType.GetGenericTypeDefinition() is var open
        && (open == typeof(Task<>) || open == typeof(ValueTask<>))
            ? returnType.GetGenericArguments()[0]
        : returnType;

    private static string Describe(Type? type) =>
        type is null ? "nothing"
        : type.IsGenericType ? $"{ShippedCode.SimpleName(type)}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>"
        : type.Name;
}
