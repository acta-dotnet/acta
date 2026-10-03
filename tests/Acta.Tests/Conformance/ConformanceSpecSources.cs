using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acta.Tests.Conformance;

/// <summary>
/// The shared conformance specs, read from source under <c>tests/Acta.Tests.Conformance</c>. Every provider
/// test project compiles them, so no one assembly holds them for reflection. A spec is a class that
/// declares its own <c>[Fact]</c> or <c>[Theory]</c> methods.
/// </summary>
internal static class ConformanceSpecSources
{
    internal sealed record Spec(string Name, ContractInfo? Contract, IReadOnlyList<FactInfo> Facts)
    {
        /// <summary>The guarantees the spec proves: each fact's display name, in declaration order.</summary>
        public IReadOnlyList<string> Guarantees =>
            [.. Facts.Select(f => f.DisplayName).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!)];
    }

    internal sealed record ContractInfo(string Id, string Title, string Area, string Contract);

    internal sealed record FactInfo(string Method, string? DisplayName);

    private static readonly Lazy<IReadOnlyList<Spec>> Specs = new(Read);

    internal static IReadOnlyList<Spec> All => Specs.Value;

    private static IReadOnlyList<Spec> Read()
    {
        var root = Path.Combine(IntegrationConfig.FindRepoRoot(), "tests", "Acta.Tests.Conformance");
        var specs = new List<Spec>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar) || relative.StartsWith("obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var facts = type
                    .Members.OfType<MethodDeclarationSyntax>()
                    .Select(m => (Method: m, Fact: Attribute(m.AttributeLists, "Fact") ?? Attribute(m.AttributeLists, "Theory")))
                    .Where(m => m.Fact is not null)
                    .Select(m => new FactInfo(m.Method.Identifier.ValueText, Named(m.Fact!, "DisplayName")))
                    .ToList();
                if (facts.Count > 0)
                {
                    specs.Add(new Spec(type.Identifier.ValueText, Contract(type), facts));
                }
            }
        }

        return specs;
    }

    private static ContractInfo? Contract(ClassDeclarationSyntax type) =>
        Attribute(type.AttributeLists, "ConformanceSpec") is not { ArgumentList.Arguments: var args } attribute
            ? null
            : new ContractInfo(
                Text(args[0].Expression),
                Text(args[1].Expression),
                Named(attribute, "Area") ?? "",
                Named(attribute, "Contract") ?? ""
            );

    private static AttributeSyntax? Attribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(list => list.Attributes).FirstOrDefault(a => a.Name.ToString() is var n && (n == name || n == name + "Attribute"));

    private static string? Named(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name) is { } argument
            ? Text(argument.Expression)
            : null;

    // Attribute arguments are string literals, or literals joined with '+'.
    private static string Text(ExpressionSyntax expression) =>
        expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) => Text(binary.Left) + Text(binary.Right),
            _ => throw new InvalidOperationException($"Not a string literal: {expression} ({expression.GetLocation().GetLineSpan()})"),
        };
}
