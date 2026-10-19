using System.Reflection;

namespace Acta.Emit.Shared.Model;

/// <summary>
/// Reflects over the <c>Acta</c> SDK assembly to enumerate its <c>[CodeKind]</c> families, the enums with a
/// source-generated <c>*Extensions</c> companion.
/// </summary>
internal static class CodeFamilyDiscovery
{
    public static IReadOnlyList<CodeFamilyModel> DiscoverAll()
    {
        var asm = typeof(Acta.JobStatusCode).Assembly;
        return asm.GetTypes()
            .Where(type => type.IsEnum)
            .Select(enumType => TryReadActaCodeFamily(enumType, asm))
            .OfType<CodeFamilyModel>()
            .OrderBy(family => family.Name, StringComparer.Ordinal)
            .ToList();
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
        if (manifestField?.GetValue(null) is not IReadOnlyList<Acta.CodeManifestEntry> { Count: > 0 } manifest)
        {
            return null;
        }

        var values = manifest
            .Select(entry => new CodeEntryModel(
                entry.Id,
                Enum.GetName(enumType, entry.Id) ?? throw new InvalidOperationException($"No {enumType.Name} member has id {entry.Id}."),
                entry.Code,
                entry.Description,
                entry.Lifecycle.ToString()
            ))
            .ToList();
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
            CodeKind: manifest[0].CodeKind,
            Values: values.OrderBy(value => value.Id).ToList(),
            ReservedCodes: reservedCodes,
            ReservedRanges: reservedRanges
        );
    }
}
