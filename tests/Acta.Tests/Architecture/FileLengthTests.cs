using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Caps every C# file under <c>src/</c> at 600 lines and every method body at 150 lines. Files and methods that
/// predate the caps are listed in the <c>length</c> and <c>method-length</c> ratchets. Once the method list is empty,
/// MA0051 takes over the method cap in the build.
/// </summary>
public sealed class FileLengthTests
{
    private const int MaxFileLines = 600;

    private const int MaxMethodLines = 150;

    [Fact]
    public void Source_files_stay_within_the_line_cap()
    {
        var tooLong = ShippedCode
            .SourceFiles("*.cs")
            .Where(file => File.ReadLines(file.Path).Count() > MaxFileLines)
            .Select(file => (Path.GetFileName(file.Path), (string?)file.Relative));

        Ratchet.Check("length", tooLong, $"Split the file so it stays within {MaxFileLines} lines.");
    }

    [Fact]
    public void Methods_stay_within_the_line_cap()
    {
        var tooLong = ShippedCode
            .SourceFiles("*.cs")
            .SelectMany(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file.Path)).GetRoot().DescendantNodes())
            .Where(node => Body(node) is { } body && BodyLines(body) > MaxMethodLines)
            .Select(Name);

        Ratchet.Check("method-length", tooLong, $"Split the method so its body stays within {MaxMethodLines} lines.");
    }

    private static SyntaxNode? Body(SyntaxNode node) =>
        node switch
        {
            BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
            AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
            LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
            _ => null,
        };

    // The body's span from first to last line, as MA0051 counts it.
    private static int BodyLines(SyntaxNode body)
    {
        var span = body.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line;
    }

    // Type.Method, which survives a file move or a namespace change.
    private static string Name(SyntaxNode node)
    {
        var name = node switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            LocalFunctionStatementSyntax local => local.Identifier.Text,
            AccessorDeclarationSyntax accessor => accessor.Keyword.Text,
            _ => node.Kind().ToString(),
        };
        var owners = node.Ancestors()
            .Select(ancestor =>
                ancestor switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.Text,
                    MethodDeclarationSyntax method => method.Identifier.Text,
                    PropertyDeclarationSyntax property => property.Identifier.Text,
                    _ => null,
                }
            )
            .OfType<string>()
            .Reverse();
        return string.Join(".", owners.Append(name));
    }
}
