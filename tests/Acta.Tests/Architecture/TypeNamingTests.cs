using System.Reflection;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds every shipped type to the suffix table in <c>docs/internals/naming-conventions.md</c>. Names that predate
/// the table are listed in the <c>naming</c> ratchet.
/// </summary>
public sealed class TypeNamingTests
{
    private static readonly HashSet<string> BannedSuffixes =
    [
        "Result",
        "Dto",
        "Data",
        "Info",
        "Model",
        "Record",
        "Item",
        "Store",
        "Service",
        "Api",
        "Page",
        "Decision",
        "Outcome",
    ];

    // Outcome stays as a domain noun for how a job, a child, or a wait ended, in handler code.
    private static readonly HashSet<string> DomainOutcomes =
    [
        "JobOutcome",
        "ChildJobOutcome",
        "MapOutcome",
        "MapItemOutcome",
        "ParallelOutcome",
        "JoinOutcome",
        "ChildWaitOutcome",
        "ChildrenWaitOutcome",
        "SignalWaitOutcome",
    ];

    [Fact]
    public void Type_names_take_the_suffix_of_their_role()
    {
        var projected = ProjectedTypes();
        var violations = ShippedCode.Types.SelectMany(type =>
            Violations(type, projected).Select(reason => $"{ShippedCode.Key(type)} ({reason})")
        );

        Ratchet.Check("naming", violations, "Rename the type by the suffix table in docs/internals/naming-conventions.md.");
    }

    private static IEnumerable<string> Violations(Type type, HashSet<Type> projected)
    {
        var name = ShippedCode.SimpleName(type);
        var suffix = LastWord(name);
        var banned = suffix switch
        {
            "Result" => name != "PagedResult",
            "Item" => !name.EndsWith("ListItem", StringComparison.Ordinal) && !SetElement(type, name),
            "Outcome" => !DomainOutcomes.Contains(name),
            _ => BannedSuffixes.Contains(suffix),
        };
        if (banned)
        {
            yield return suffix;
        }
        if (name.EndsWith("PageRequest", StringComparison.Ordinal))
        {
            yield return "PageRequest";
        }
        if (suffix == "Row" && !projected.Contains(type))
        {
            yield return "Row without DbProjection";
        }
        // Entity is the table model's role: every [DbTable] class takes it, and nothing else does but IEntity.
        var table = type.GetCustomAttributesData().Any(a => a.AttributeType.Name == "DbTableAttribute");
        if (table != (suffix == "Entity") && name != "IEntity")
        {
            yield return table ? "DbTable without Entity" : "Entity outside a table model";
        }
    }

    // Item also names the element of a Set: TagSet is a list of TagItem.
    private static bool SetElement(Type type, string name) =>
        type.Assembly.GetType($"{type.Namespace}.{name[..^"Item".Length]}Set") is { } set
        && typeof(IReadOnlyList<>).MakeGenericType(type).IsAssignableFrom(set);

    // A row is projected when the generator emitted its binder, Bind<Type>, whether the type declares
    // [DbProjection] or the code only resolves it.
    private static HashSet<Type> ProjectedTypes() =>
        [
            .. ShippedCode
                .Types.SelectMany(type =>
                    type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                )
                .Where(method => method.Name == "Bind" + method.ReturnType.Name)
                .Select(method => method.ReturnType),
        ];

    // The last PascalCase word: "Outcome" in CompletionKind, "Dto" in OutboxMetaJson.
    private static string LastWord(string name)
    {
        for (var i = name.Length - 1; i > 0; i--)
        {
            if (char.IsUpper(name[i]) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                return name[i..];
            }
        }
        return name;
    }
}
