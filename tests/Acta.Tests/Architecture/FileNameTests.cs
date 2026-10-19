using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds the shipped code in <c>src/</c> to the file rule in <c>CONTRIBUTING.md</c> (Source layout): a file is named for its first
/// top-level type, and a partial type's other files for that type and a part, <c>Type.Part.cs</c>. Which other types may
/// share the file turns on who references them, which a name check cannot see.
/// </summary>
public sealed class FileNameTests
{
    private const string Root = "src/";

    [Fact]
    public void A_file_is_named_for_its_first_type()
    {
        var misnamed = ShippedCode
            .SourceFiles("*.cs")
            .Where(file => file.Relative.StartsWith(Root, StringComparison.Ordinal))
            .Select(file => (file.Relative, First: FirstType(file.Path)))
            .Where(file => file.First is not null && Path.GetFileName(file.Relative).Split('.')[0] != file.First)
            .Select(file => (file.Relative, (string?)$"declares {file.First} first"));

        ArchitectureAssert.NoViolations(misnamed, "Name the file for its first top-level type, or move that type to a file named for it.");
    }

    // The first type or delegate declared at the top of the file, inside its namespace; null for a file that declares none.
    private static string? FirstType(string path) =>
        CSharpSyntaxTree
            .ParseText(File.ReadAllText(path))
            .GetCompilationUnitRoot()
            .Members.SelectMany(member => member is BaseNamespaceDeclarationSyntax ns ? ns.Members : [member])
            .Select(member =>
                member switch
                {
                    BaseTypeDeclarationSyntax type => type.Identifier.Text,
                    DelegateDeclarationSyntax type => type.Identifier.Text,
                    _ => null,
                }
            )
            .FirstOrDefault(name => name is not null);
}
