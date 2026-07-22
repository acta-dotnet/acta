using System.Globalization;
using System.Reflection;

namespace Acta.Emit.Shared.Model;

/// <summary>
/// One discovered code family (Code or meta-enum): name, storage width, and its values.
/// </summary>
internal sealed record CodeFamilyModel(
    string Name,
    string? CodeKind,
    string Storage,
    IReadOnlyList<CodeEntryModel> Values,
    string? Summary,
    CodeCapacityModel? Capacity,
    bool IsMeta = false
);

/// <summary>
/// One value inside a <see cref="CodeFamilyModel"/>.
/// </summary>
internal sealed record CodeEntryModel(byte Id, string Code, string Description, string Lifecycle);

internal sealed record CodeCapacityModel(
    int Assigned,
    int Deprecated,
    int Retired,
    int PermanentlyReserved,
    int HeldReserve,
    int Available,
    int InvalidSentinels
)
{
    public int Consumed => Assigned + Deprecated + Retired + PermanentlyReserved;
}

/// <summary>
/// Reflects over <c>Acta.Contracts</c> to enumerate <c>[CodeKind]</c> families and
/// the meta-enums referenced by enum-backed (coded) columns.
/// </summary>
internal static class CodeFamilyDiscovery
{
    public static IReadOnlyList<CodeFamilyModel> DiscoverAll(SchemaModel? schema = null)
    {
        var asm = typeof(Acta.JobStatusCode).Assembly;
        var docs = XmlDocSource.ForAssembly(asm);

        var families = new List<CodeFamilyModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // --- (1) Code families (have source-generated *Extensions companions). ---
        foreach (var enumType in asm.GetTypes().Where(t => t.IsEnum))
        {
            var family = TryReadActaCodeFamily(enumType, asm, docs);
            if (family is null)
            {
                continue;
            }
            families.Add(family);
            seen.Add(family.Name);
        }

        // --- (2) Meta-enums: byte/short enums referenced by Code or Byte columns but lacking a
        // generated companion. These have no code_kind discriminator but DO need a doc link target. ---
        if (schema is not null)
        {
            foreach (var entity in schema.Entities)
            {
                foreach (var col in entity.Columns)
                {
                    if (col.EnumTypeName is null)
                    {
                        continue;
                    }
                    if (seen.Contains(col.EnumTypeName))
                    {
                        continue;
                    }
                    var enumType =
                        asm.GetType($"Acta.{col.EnumTypeName}")
                        ?? asm.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name == col.EnumTypeName);
                    if (enumType is null)
                    {
                        continue;
                    }

                    var family = ReadMetaEnumFamily(enumType, docs);
                    families.Add(family);
                    seen.Add(family.Name);
                }
            }
        }

        return families
            .OrderBy(f => CodeFamilyInference.DomainOrder(CodeFamilyInference.DomainFor(f.Name)))
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static CodeFamilyModel? TryReadActaCodeFamily(Type enumType, Assembly asm, XmlDocSource docs)
    {
        var extName = $"{enumType.Namespace}.{enumType.Name}Extensions";
        var extType = asm.GetType(extName);
        if (extType is null)
        {
            return null;
        }

        var manifestField = extType.GetField("_manifest", BindingFlags.NonPublic | BindingFlags.Static);
        if (manifestField?.GetValue(null) is not Array manifest || manifest.Length == 0)
        {
            return null;
        }

        string? codeKind = null;
        var values = new List<CodeEntryModel>();
        foreach (var entry in manifest)
        {
            if (entry is null)
            {
                continue;
            }
            var t = entry.GetType();
            codeKind ??= t.GetProperty("CodeKind")?.GetValue(entry) as string;
            var id = Convert.ToByte(t.GetProperty("Id")?.GetValue(entry) ?? (byte)0, CultureInfo.InvariantCulture);
            var code = t.GetProperty("Code")?.GetValue(entry) as string ?? "";
            var description = t.GetProperty("Description")?.GetValue(entry) as string ?? "";
            var lifecycle = t.GetProperty("Lifecycle")?.GetValue(entry)?.ToString() ?? "Active";

            values.Add(new CodeEntryModel(id, code, description, lifecycle));
        }

        if (codeKind is null)
        {
            return null;
        }

        var storage = enumType.GetEnumUnderlyingType() == typeof(byte) ? "byte" : "short";

        var tombstones = enumType.GetCustomAttributes<Acta.ReservedCodeAttribute>().ToArray();
        var ranges = enumType.GetCustomAttributes<Acta.ReservedCodeRangeAttribute>().ToArray();
        var assigned = values.Count(v => v.Lifecycle == "Active");
        var deprecated = values.Count(v => v.Lifecycle == "Deprecated");
        var retired = values.Count(v => v.Lifecycle == "Retired") + tombstones.Length;
        var permanentlyReserved = ranges.Where(r => r.PermanentlyUnavailable).Sum(r => r.End - r.Start + 1);
        var heldReserve = ranges.Where(r => !r.PermanentlyUnavailable).Sum(r => r.End - r.Start + 1);
        var zeroUsable = values.Any(v => v.Id == 0) || tombstones.Any(r => r.Id == 0) || ranges.Any(r => r.Start == 0);
        var usable = zeroUsable ? 255 : 254;
        var available = usable - assigned - deprecated - retired - permanentlyReserved - heldReserve;

        return new CodeFamilyModel(
            Name: enumType.Name,
            CodeKind: codeKind,
            Storage: storage,
            Values: values.OrderBy(v => v.Id).ToList(),
            Summary: docs.ForType(enumType),
            Capacity: new CodeCapacityModel(assigned, deprecated, retired, permanentlyReserved, heldReserve, available, zeroUsable ? 1 : 2),
            IsMeta: false
        );
    }

    private static CodeFamilyModel ReadMetaEnumFamily(Type enumType, XmlDocSource docs)
    {
        var values = new List<CodeEntryModel>();
        foreach (var name in Enum.GetNames(enumType))
        {
            var raw = Enum.Parse(enumType, name);
            var id = Convert.ToByte(raw, CultureInfo.InvariantCulture);
            var field = enumType.GetField(name)!;
            var description = docs.ForField(field) ?? "";

            // Render member name as kebab-ish identifier (camel/Pascal → kebab).
            var code = ToKebab(name);
            values.Add(new CodeEntryModel(id, code, description, Lifecycle: "Active"));
        }

        var storage = enumType.GetEnumUnderlyingType() == typeof(byte) ? "byte" : "short";

        return new CodeFamilyModel(
            Name: enumType.Name,
            CodeKind: null, // meta-enums have no code_kind discriminator, hence no code_kind discriminator
            Storage: storage,
            Values: values.OrderBy(v => v.Id).ToList(),
            Summary: docs.ForType(enumType),
            Capacity: null,
            IsMeta: true
        );
    }

    private static string ToKebab(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (i > 0 && char.IsUpper(c))
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
