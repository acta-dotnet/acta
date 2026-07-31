using System.Globalization;
using System.Reflection;

namespace Acta.Emit.Shared.Model;

/// <summary>One discovered code family (Code or meta-enum) and its persisted values.</summary>
internal sealed record CodeFamilyModel(
    string Name,
    string? CodeKind,
    string Storage,
    IReadOnlyList<CodeEntryModel> Values,
    IReadOnlyList<ReservedCodeModel> ReservedCodes,
    IReadOnlyList<ReservedCodeRangeModel> ReservedRanges,
    bool IsMeta = false
);

internal sealed record CodeEntryModel(byte Id, string Member, string Code, string Description, string Lifecycle);

internal sealed record ReservedCodeModel(byte Id, string Code);

internal sealed record ReservedCodeRangeModel(byte Start, byte End, string Reason, bool PermanentlyUnavailable);

/// <summary>
/// Reflects over the <c>Acta</c> SDK assembly to enumerate <c>[CodeKind]</c> families and
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
        foreach (var enumType in asm.GetTypes().Where(type => type.IsEnum))
        {
            var family = TryReadActaCodeFamily(enumType, asm);
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
                foreach (var column in entity.Columns)
                {
                    if (column.EnumTypeName is null || seen.Contains(column.EnumTypeName))
                    {
                        continue;
                    }
                    var enumType =
                        asm.GetType($"Acta.{column.EnumTypeName}")
                        ?? asm.GetTypes().FirstOrDefault(type => type.IsEnum && type.Name == column.EnumTypeName);
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

        return families.OrderBy(family => family.Name, StringComparer.Ordinal).ToList();
    }

    private static CodeFamilyModel? TryReadActaCodeFamily(Type enumType, Assembly asm)
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
            var entryType = entry.GetType();
            codeKind ??= entryType.GetProperty("CodeKind")?.GetValue(entry) as string;
            var id = Convert.ToByte(entryType.GetProperty("Id")?.GetValue(entry) ?? (byte)0, CultureInfo.InvariantCulture);
            var member = Enum.GetName(enumType, id) ?? throw new InvalidOperationException($"No {enumType.Name} member has id {id}.");
            var code = entryType.GetProperty("Code")?.GetValue(entry) as string ?? "";
            var description = entryType.GetProperty("Description")?.GetValue(entry) as string ?? "";
            var lifecycle = entryType.GetProperty("Lifecycle")?.GetValue(entry)?.ToString() ?? "Active";

            values.Add(new CodeEntryModel(id, member, code, description, lifecycle));
        }

        if (codeKind is null)
        {
            return null;
        }

        var storage = enumType.GetEnumUnderlyingType() == typeof(byte) ? "byte" : "short";
        var reservedCodes = enumType
            .GetCustomAttributes<Acta.ReservedCodeAttribute>()
            .OrderBy(code => code.Id)
            .Select(code => new ReservedCodeModel(code.Id, code.Code))
            .ToList();
        var reservedRanges = enumType
            .GetCustomAttributes<Acta.ReservedCodeRangeAttribute>()
            .OrderBy(range => range.Start)
            .Select(range => new ReservedCodeRangeModel(range.Start, range.End, range.Reason, range.PermanentlyUnavailable))
            .ToList();

        return new CodeFamilyModel(
            Name: enumType.Name,
            CodeKind: codeKind,
            Storage: storage,
            Values: values.OrderBy(value => value.Id).ToList(),
            ReservedCodes: reservedCodes,
            ReservedRanges: reservedRanges,
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

            values.Add(new CodeEntryModel(id, name, ToKebab(name), description, Lifecycle: "Active"));
        }

        var storage = enumType.GetEnumUnderlyingType() == typeof(byte) ? "byte" : "short";

        return new CodeFamilyModel(
            Name: enumType.Name,
            CodeKind: null,
            Storage: storage,
            Values: values.OrderBy(value => value.Id).ToList(),
            ReservedCodes: [],
            ReservedRanges: [],
            IsMeta: true
        );
    }

    private static string ToKebab(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var character = pascal[i];
            if (i > 0 && char.IsUpper(character))
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(character));
        }
        return sb.ToString();
    }
}
