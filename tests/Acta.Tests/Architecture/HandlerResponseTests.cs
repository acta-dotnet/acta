using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds operations to "every operation has its own input and its own response" in <c>docs/internals/naming-conventions.md</c>:
/// <c>XHandler.HandleAsync</c> takes <c>XCommand</c> or <c>XQuery</c> and returns <c>XResponse</c> for a command, and a row,
/// command, or query is declared in its operation's folder under the operation's name, a row named by no other
/// operation. What predates a rule is listed in its ratchet, <c>handler-response</c> or <c>slice-members</c>.
/// </summary>
public sealed class HandlerResponseTests
{
    private const string Operations = "src/Acta.Runtime/Operations/";

    // What an operation owns outright; of these, only a row is also kept from every other operation.
    private static readonly string[] SliceRoles = ["Row", "Command", "Query"];

    [Fact]
    public void A_handler_takes_its_operations_input_and_returns_its_response()
    {
        var handlers = ShippedCode
            .Types.Where(type => type.Name.EndsWith("Handler", StringComparison.Ordinal))
            .SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(method => method.Name == "HandleAsync")
            )
            .Select(method =>
                (Handler: method.DeclaringType!, Input: method.GetParameters()[0].ParameterType, Returns: Awaited(method.ReturnType))
            )
            .ToList();

        // An operation with no input takes only the token.
        var inputs = handlers
            .Where(handler => handler.Input != typeof(CancellationToken) && !OwnInput(handler.Handler, handler.Input))
            .Select(handler =>
                ($"{ShippedCode.Key(handler.Handler)} (takes {Describe(handler.Input)})", (string?)"HandleAsync takes another input")
            );
        var responses = handlers
            .Where(handler => handler.Input.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(handler => handler.Returns?.Name != handler.Input.Name[..^"Command".Length] + "Response")
            .Select(handler => (ShippedCode.Key(handler.Handler), (string?)$"{handler.Input.Name} returns {Describe(handler.Returns)}"));

        Ratchet.Check(
            "handler-response",
            inputs.Concat(responses),
            "Take the operation's own XCommand or XQuery in HandleAsync, and return its own XResponse from a command."
        );
    }

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
            .Where(file => file.Relative.StartsWith(Operations, StringComparison.Ordinal))
            .Select(file => (Slice: Slice(file.Relative), Root: CSharpSyntaxTree.ParseText(File.ReadAllText(file.Path)).GetRoot()))
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

        var violations = files.SelectMany(file =>
            file.Root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .SelectMany(type => SliceViolations(type.Identifier.Text, file.Slice, users))
        );

        Ratchet.Check(
            "slice-members",
            violations,
            "Declare the type in its operation's folder, named for the operation, and keep a row to that operation."
        );
    }

    // "Group/Operation" for a file in an operation folder, null for a group-level file.
    private static string? Slice(string relative) =>
        relative[Operations.Length..].Split('/') is [var group, var operation, _] ? $"{group}/{operation}" : null;

    private static IEnumerable<(string Key, string? Detail)> SliceViolations(string name, string? slice, ILookup<string, string> users)
    {
        var role = SliceRoles.FirstOrDefault(role => name.EndsWith(role, StringComparison.Ordinal));
        if (role is null)
        {
            yield break;
        }
        if (slice is null)
        {
            yield return ($"{name} (outside a slice)", "declared in a group-level file");
            yield break;
        }

        var operation = slice[(slice.IndexOf('/') + 1)..];
        if (role == "Row" ? !name.StartsWith(operation, StringComparison.Ordinal) : name != operation + role)
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

    // What the caller awaits: the T of Task<T> or ValueTask<T>, nothing for the plain ones.
    private static Type? Awaited(Type returnType) => returnType.IsGenericType ? returnType.GetGenericArguments()[0] : null;

    private static string Describe(Type? type) =>
        type is null ? "nothing"
        : type.IsGenericType ? $"{ShippedCode.SimpleName(type)}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>"
        : type.Name;
}
