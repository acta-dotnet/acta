using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Fails when two shipped types declare the same set of property names, which is how a row, its outcome, and its
/// public model end up as three copies of one shape. A type declared in an operation's folder belongs to that
/// operation, and a route's request to its folder in <c>Acta.AspNetCore</c>, so types of different folders may match
/// on purpose. Copies that predate the rule are listed in the <c>duplicate-shape</c> ratchet, one entry per copy.
/// </summary>
public sealed class DuplicateShapeTests
{
    // Below three properties, shared names are coincidence rather than copying.
    private const int MinProperties = 3;

    // Each project's operation folders, by the namespace its types are declared in. A route's folder is its own owner,
    // apart from the operation of the same name: Acta.AspNetCore sees only the public contract, so a request that
    // carries a command's members over the wire is the copy it has to be.
    private static readonly (string Root, string Namespace)[] OperationRoots =
    [
        ("src/Acta/Operations/", "Acta"),
        ("src/Acta.AspNetCore/Operations/", "Acta.AspNetCore"),
    ];

    [Fact]
    public void No_two_types_share_one_shape()
    {
        var owners = Owners();
        var copies = ShippedCode
            .Types.Where(type =>
                !type.IsInterface && !typeof(Attribute).IsAssignableFrom(type) && !typeof(Exception).IsAssignableFrom(type)
            )
            .Select(type => (Type: type, Shape: Shape(type)))
            .Where(entry => entry.Shape.Count >= MinProperties)
            .GroupBy(entry => string.Join(",", entry.Shape), StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && !OwnedByDifferentOperations([.. group.Select(entry => Owner(entry.Type, owners))]))
            .SelectMany(group =>
                group.Select(entry =>
                    (
                        ShippedCode.Key(entry.Type),
                        (string?)(
                            "same shape as "
                            + string.Join(", ", group.Where(other => other.Type != entry.Type).Select(other => ShippedCode.Key(other.Type)))
                        )
                    )
                )
            );

        Ratchet.Check("duplicate-shape", copies, "Return the existing type instead of copying it; SQL can fill a public model directly.");
    }

    // Every operation has its own input, response, row, and parts, so two operations may match on purpose: a Get's
    // response and its subject's list element are two types even where they match today. Two copies in one operation,
    // or outside the operations, are still one shape too many.
    private static bool OwnedByDifferentOperations(IReadOnlyList<string?> owners) =>
        owners.All(owner => owner is not null) && owners.Distinct(StringComparer.Ordinal).Count() == owners.Count;

    // "Group/Operation" of the folder that declares the type, or of its outermost containing type.
    private static string? Owner(Type type, Dictionary<string, string> owners)
    {
        var outer = type;
        while (outer.DeclaringType is { } container)
        {
            outer = container;
        }
        return owners.GetValueOrDefault($"{outer.Namespace}.{ShippedCode.SimpleName(outer)}");
    }

    // Every top-level type declared in <root>Operations/<Group>/<Operation>/, by namespace-qualified name; each project
    // has one namespace. The owner is the folder, project included.
    private static Dictionary<string, string> Owners() =>
        ShippedCode
            .SourceFiles("*.cs")
            .SelectMany(file =>
                OperationRoots
                    .Where(root => file.Relative.StartsWith(root.Root, StringComparison.Ordinal))
                    .Select(root => (file.Path, root.Root, root.Namespace, Below: file.Relative[root.Root.Length..].Split('/')))
            )
            .Where(file => file.Below.Length == 3)
            .SelectMany(file =>
                CSharpSyntaxTree
                    .ParseText(File.ReadAllText(file.Path))
                    .GetRoot()
                    .DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .Where(declaration => declaration.Parent is not BaseTypeDeclarationSyntax)
                    .Select(declaration =>
                        (Key: $"{file.Namespace}.{declaration.Identifier.Text}", Owner: $"{file.Root}{file.Below[0]}/{file.Below[1]}")
                    )
            )
            .ToDictionary(entry => entry.Key, entry => entry.Owner, StringComparer.Ordinal);

    // Only what the type declares itself: a derived type or an interface implementation repeats a shape on purpose.
    private static List<string> Shape(Type type)
    {
        var declared = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToList();
        if (declared.Count < MinProperties)
        {
            return [];
        }
        var implemented = type.GetInterfaces().SelectMany(contract => type.GetInterfaceMap(contract).TargetMethods).ToHashSet();
        return
        [
            .. declared
                .Where(property => !implemented.Contains(property.GetMethod!))
                .Select(property => property.Name.ToUpperInvariant())
                .Order(StringComparer.Ordinal),
        ];
    }
}
