using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Acta.Generators.Features.Jobs;

/// <summary>
/// Builds the compile-time JSON skeleton emitted on <c>JobDescriptor.InputTemplateJson</c>: the shape
/// of a job's input as the operator dashboard's enqueue editor should seed it. Every value is an
/// empty stand-in, never a default the runtime honors. Names follow the framework wire shape
/// (camelCase, <c>[JsonPropertyName]</c> wins) so a seeded editor round-trips through
/// <c>JsonJobPayloadSerializer</c>.
/// </summary>
internal static class InputTemplateJson
{
    // Depth 1 is the input type itself. Deeper nesting emits null rather than a shape: the hint is a
    // starting point for a human, not a schema, and unbounded recursion would hang the compiler.
    private const int MaxDepth = 3;

    /// <summary>
    /// Compact JSON object for <paramref name="inputType"/>, or null when the type contributes no
    /// usable members (the caller then emits no template at all).
    /// </summary>
    public static string? Build(ITypeSymbol inputType)
    {
        var sb = new StringBuilder();
        return WriteObject(sb, inputType, depth: 1, path: new List<ITypeSymbol>()) ? sb.ToString() : null;
    }

    // False when the type has no settable members; the caller distinguishes "no shape" from "{}".
    private static bool WriteObject(StringBuilder sb, ITypeSymbol type, int depth, List<ITypeSymbol> path)
    {
        var members = Members(type).ToList();
        if (members.Count == 0)
        {
            return false;
        }

        path.Add(type);
        sb.Append('{');
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append('"').Append(Escape(WireName(members[i]))).Append("\":");
            WriteValue(sb, members[i].Type, depth, path);
        }
        sb.Append('}');
        path.RemoveAt(path.Count - 1);
        return true;
    }

    private static void WriteValue(StringBuilder sb, ITypeSymbol type, int depth, List<ITypeSymbol> path)
    {
        // Nullable<T>, annotated reference types, enums, and every string-shaped scalar (dates, Guid,
        // TimeSpan) read better as null than as a fabricated value the operator must then correct.
        if (
            type.NullableAnnotation == NullableAnnotation.Annotated
            || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || type.TypeKind == TypeKind.Enum
            || type.TypeKind == TypeKind.TypeParameter
        )
        {
            sb.Append("null");
            return;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                sb.Append("false");
                return;
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                sb.Append('0');
                return;
            case SpecialType.System_String:
            case SpecialType.System_Char:
            case SpecialType.System_DateTime:
                sb.Append("null");
                return;
        }

        var name = type.OriginalDefinition.ToDisplayString();
        if (name is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan" or "System.DateOnly" or "System.TimeOnly" or "System.Uri")
        {
            sb.Append("null");
            return;
        }

        // A dictionary is an IEnumerable of pairs but serializes as a JSON object, so it must not fall
        // into the array branch below.
        if (IsDictionary(type))
        {
            sb.Append("{}");
            return;
        }

        if (IsCollection(type))
        {
            sb.Append("[]");
            return;
        }

        if (depth >= MaxDepth || path.Any(t => SymbolEqualityComparer.Default.Equals(t, type)))
        {
            sb.Append("null");
            return;
        }

        if (!WriteObject(sb, type, depth + 1, path))
        {
            sb.Append("null");
        }
    }

    // Public instance properties carrying a setter or init, base types first. Record positional
    // parameters arrive here as the init-only properties the compiler synthesizes for them.
    private static IEnumerable<IPropertySymbol> Members(ITypeSymbol type)
    {
        var chain = new List<ITypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            chain.Insert(0, t);
        }

        return chain.SelectMany(t =>
            t.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p =>
                    !p.IsStatic
                    && !p.IsIndexer
                    && p.DeclaredAccessibility == Accessibility.Public
                    && p.SetMethod is { DeclaredAccessibility: Accessibility.Public }
                    && !p.GetAttributes()
                        .Any(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute")
                )
        );
    }

    private static bool IsCollection(ITypeSymbol type) =>
        type is IArrayTypeSymbol
        || type.AllInterfaces.Any(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_IEnumerable);

    private static bool IsDictionary(ITypeSymbol type) => IsDictionaryName(type) || type.AllInterfaces.Any(IsDictionaryName);

    private static bool IsDictionaryName(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString()
            is "System.Collections.IDictionary"
                or "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";

    private static string WireName(IPropertySymbol property)
    {
        var attribute = property
            .GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is string explicitName && explicitName.Length > 0)
        {
            return explicitName;
        }
        return CamelCase(property.Name);
    }

    // Mirrors JsonNamingPolicy.CamelCase, which lowercases a leading run of capitals but keeps the
    // last one when a lowercase letter follows it ("IPAddress" -> "ipAddress", "ID" -> "id").
    private static string CamelCase(string name)
    {
        if (name.Length == 0 || !char.IsUpper(name[0]))
        {
            return name;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
            {
                break;
            }
            if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1]))
            {
                break;
            }
            chars[i] = char.ToLowerInvariant(chars[i]);
        }
        return new string(chars);
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
