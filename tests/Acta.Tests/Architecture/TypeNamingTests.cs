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
        "Entity",
        "Record",
        "Item",
        "Store",
        "Service",
        "Api",
        "Page",
        "Decision",
        "Outcome",
    ];

    // Outcome stays as a domain noun for how a job or a child ended, in handler code.
    private static readonly HashSet<string> DomainOutcomes =
    [
        "JobOutcome",
        "ChildJobOutcome",
        "MapOutcome",
        "MapItemOutcome",
        "ParallelOutcome",
        "JoinOutcome",
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
            "Item" => !name.EndsWith("ListItem", StringComparison.Ordinal),
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
        if (suffix != "Table" && type.GetCustomAttributesData().Any(a => a.AttributeType.Name == "DbTableAttribute"))
        {
            yield return "DbTable without Table";
        }
    }

    // A row is projected by [DbProjection] on the type or by an assembly-level [DbProjection(typeof(...))] list.
    private static HashSet<Type> ProjectedTypes() =>
        [
            .. ShippedCode
                .Assemblies.SelectMany(assembly => assembly.GetCustomAttributesData())
                .Where(IsDbProjection)
                .SelectMany(attribute => (IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value!)
                .Select(argument => (Type)argument.Value!)
                .Concat(ShippedCode.Types.Where(type => type.GetCustomAttributesData().Any(IsDbProjection))),
        ];

    private static bool IsDbProjection(CustomAttributeData attribute) => attribute.AttributeType.Name == "DbProjectionAttribute";

    // The last PascalCase word: "Store" in IAlertStore, "Dto" in OutboxMetaDto.
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
