using System.Linq;
using Microsoft.CodeAnalysis;

namespace Acta.Generators.Shared;

/// <summary>
/// Names the relational generators share with CodeGenerator's output, so a row binder can call into a
/// code family's generated extensions.
/// </summary>
internal static class CodeFamilyNames
{
    /// <summary>
    /// The generated <c>{Enum}Extensions</c> class of an <c>[CodeKind(Extensible = true)]</c> enum, or null
    /// for any other type. Its <c>FromId</c> maps an id this build does not know to the id-0 member,
    /// which a bare cast would leave as an undefined value. CodeGenerator emits the class into the enum's
    /// namespace, never into a containing type.
    /// </summary>
    public static string? ExtensibleDecoderFqn(INamedTypeSymbol enumType)
    {
        var codeKind = enumType.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CodeKindAttribute");
        if (codeKind?.NamedArguments.FirstOrDefault(a => a.Key == "Extensible").Value.Value is not true)
        {
            return null;
        }

        var ns = enumType.ContainingNamespace;
        var prefix = ns.IsGlobalNamespace ? "global::" : "global::" + ns.ToDisplayString() + ".";
        return prefix + enumType.Name + "Extensions";
    }
}
