using System.Reflection;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Fails when two shipped types declare the same set of property names, which is how a row, its outcome, and its
/// public model end up as three copies of one shape. Copies that predate the rule are listed in the
/// <c>duplicate-shape</c> ratchet, one entry per copy.
/// </summary>
public sealed class DuplicateShapeTests
{
    // Below three properties, shared names are coincidence rather than copying.
    private const int MinProperties = 3;

    // Every operation has its own input and response, so two operations may match on purpose.
    private static readonly string[] PerOperationSuffixes = ["Command", "Query", "Response"];

    [Fact]
    public void No_two_types_share_one_shape()
    {
        var copies = ShippedCode
            .Types.Where(type =>
                !type.IsInterface && !typeof(Attribute).IsAssignableFrom(type) && !typeof(Exception).IsAssignableFrom(type)
            )
            .Select(type => (Type: type, Shape: Shape(type)))
            .Where(entry => entry.Shape.Count >= MinProperties)
            .GroupBy(entry => string.Join(",", entry.Shape), StringComparer.Ordinal)
            .Where(group => group.Count() > 1 && !group.All(entry => IsPerOperation(entry.Type)))
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

    private static bool IsPerOperation(Type type) =>
        PerOperationSuffixes.Any(suffix => ShippedCode.SimpleName(type).EndsWith(suffix, StringComparison.Ordinal));

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
