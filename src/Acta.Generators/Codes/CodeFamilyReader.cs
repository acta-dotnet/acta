using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Acta.Generators.CodeGenerator;

namespace Acta.Generators;

/// <summary>Reads one code family's <c>[CodeKind]</c>, members, and reservations, with the ACTA020x checks.</summary>
internal static class CodeFamilyReader
{
    /// <summary>The family's <c>[CodeKind]</c> name, ACTA0201 when it is missing or not kebab.</summary>
    internal static string ReadCodeKind(INamedTypeSymbol enumType, ImmutableArray<DiagnosticRecord>.Builder diagnostics)
    {
        // [CodeKind("kebab-name")] is required on every [Code]-bearing enum. Every
        // persisted name in Acta is explicit on its attribute; the catalog discriminator is no
        // exception. The generator derives nothing from the C# enum name: that decoupling
        // protects operator queries against silent C# renames (see proposal 0008).
        var codeKindAttr = enumType.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CodeKindAttribute");
        if (
            codeKindAttr is null
            || codeKindAttr.ConstructorArguments.Length == 0
            || codeKindAttr.ConstructorArguments[0].Value is not string declaredKind
            || string.IsNullOrEmpty(declaredKind)
        )
        {
            diagnostics.Add(
                new DiagnosticRecord(
                    "ACTA0201",
                    $"Enum `{enumType.Name}` declares `[Code]` members but is missing `[CodeKind(\"...\")]`. "
                        + $"Add `[CodeKind(\"kebab-name\")]` to the enum, where the kebab name matches the "
                        + $"stable `code_kind` discriminator documented in docs/98.",
                    Fatal: true
                )
            );
            return enumType.Name; // Placeholder: emission skips on diagnostic anyway.
        }

        if (!IsValidCodeShape(declaredKind))
        {
            diagnostics.Add(
                new DiagnosticRecord(
                    "ACTA0201",
                    $"`[CodeKind(\"{declaredKind}\")]` on `{enumType.Name}` must match kebab "
                        + $"(`[a-z][a-z0-9-]*`) optionally dotted (`.<kebab>`).",
                    Fatal: true
                )
            );
        }

        return declaredKind;
    }

    /// <summary>The family's <c>[Code]</c> members, checked one by one (ACTA0202, ACTA0203).</summary>
    internal static List<CodeModel> ReadCodes(
        INamedTypeSymbol enumType,
        IEnumerable<FieldHit> familyHits,
        ImmutableArray<DiagnosticRecord>.Builder diagnostics
    )
    {
        var codes = new List<CodeModel>();
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<short>();
        foreach (var hit in familyHits)
        {
            if (hit.OutOfRange)
            {
                diagnostics.Add(
                    new DiagnosticRecord("ACTA0202", $"`{enumType.Name}.{hit.MemberName}` value is out of short range.", Fatal: true)
                );
                continue;
            }
            if (hit.Id is < 0 or > 254)
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0202",
                        $"`{enumType.Name}.{hit.MemberName}` value `{hit.Id}` is outside the closed-byte assignment range 0..254; 255 is permanently invalid."
                    )
                );
            }
            if (!IsValidCodeShape(hit.Code))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0202",
                        $"`{enumType.Name}.{hit.MemberName}` Code `{hit.Code}` must match kebab (`[a-z][a-z0-9-]*`) optionally dotted (`.<kebab>`)."
                    )
                );
            }

            if (!seenCodes.Add(hit.Code))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0203",
                        $"`{enumType.Name}.{hit.MemberName}` Code `{hit.Code}` duplicates another member in the same family."
                    )
                );
            }

            if (!seenIds.Add(hit.Id))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0203",
                        $"`{enumType.Name}.{hit.MemberName}` value `{hit.Id}` duplicates another member in the same family."
                    )
                );
            }

            codes.Add(new CodeModel(hit.MemberName, hit.Id, hit.Code, hit.Description, hit.Lifecycle));
        }
        return codes;
    }

    internal static ImmutableArray<ReservationModel> ReadReservations(
        INamedTypeSymbol enumType,
        ImmutableArray<DiagnosticRecord>.Builder diagnostics
    )
    {
        var reservations = ImmutableArray.CreateBuilder<ReservationModel>();
        foreach (var attr in enumType.GetAttributes().Where(a => a.AttributeClass?.Name == "ReservedCodeAttribute"))
        {
            if (
                attr.ConstructorArguments.Length < 2
                || attr.ConstructorArguments[0].Value is null
                || attr.ConstructorArguments[1].Value is not string code
            )
            {
                diagnostics.Add(new DiagnosticRecord("ACTA0204", $"`{enumType.Name}` has an invalid `[ReservedCode]` declaration."));
                continue;
            }

            var id = Convert.ToInt16(attr.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
            if (id is < 0 or > 254 || !IsValidCodeShape(code))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0204",
                        $"`{enumType.Name}` reserved code `{code}` / `{id}` must use a valid textual code and an id in 0..254."
                    )
                );
            }
            reservations.Add(new ReservationModel(id, code));
        }
        return reservations.ToImmutable();
    }

    internal static ImmutableArray<ReservedRangeModel> ReadReservedRanges(
        INamedTypeSymbol enumType,
        ImmutableArray<DiagnosticRecord>.Builder diagnostics
    )
    {
        var ranges = ImmutableArray.CreateBuilder<ReservedRangeModel>();
        foreach (var attr in enumType.GetAttributes().Where(a => a.AttributeClass?.Name == "ReservedCodeRangeAttribute"))
        {
            if (
                attr.ConstructorArguments.Length < 3
                || attr.ConstructorArguments[0].Value is null
                || attr.ConstructorArguments[1].Value is null
            )
            {
                diagnostics.Add(new DiagnosticRecord("ACTA0204", $"`{enumType.Name}` has an invalid `[ReservedCodeRange]` declaration."));
                continue;
            }

            var start = Convert.ToInt16(attr.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
            var end = Convert.ToInt16(attr.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
            if (start < 0 || end > 254 || start > end)
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0204",
                        $"`{enumType.Name}` reserved range `{start}..{end}` must be ordered and contained in 0..254."
                    )
                );
            }
            ranges.Add(new ReservedRangeModel(start, end));
        }
        return ranges.ToImmutable();
    }

    internal static void ValidateReservations(
        string enumName,
        IReadOnlyList<CodeModel> codes,
        ImmutableArray<ReservationModel> reservations,
        ImmutableArray<ReservedRangeModel> ranges,
        ImmutableArray<DiagnosticRecord>.Builder diagnostics
    )
    {
        var reservedIds = new HashSet<short>();
        var reservedCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
        {
            if (!reservedIds.Add(reservation.Id) || !reservedCodes.Add(reservation.Code))
            {
                diagnostics.Add(
                    new DiagnosticRecord("ACTA0204", $"`{enumName}` repeats reserved id `{reservation.Id}` or code `{reservation.Code}`.")
                );
            }
            if (codes.Any(c => c.Id == reservation.Id || string.Equals(c.Code, reservation.Code, StringComparison.Ordinal)))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0204",
                        $"`{enumName}` reuses reserved id `{reservation.Id}` or textual code `{reservation.Code}`. Tombstones are immutable."
                    )
                );
            }
        }

        for (var i = 0; i < ranges.Length; i++)
        {
            var range = ranges[i];
            if (codes.Any(c => c.Id >= range.Start && c.Id <= range.End) || reservations.Any(r => r.Id >= range.Start && r.Id <= range.End))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0204",
                        $"`{enumName}` assigns a code or tombstone inside reserved range `{range.Start}..{range.End}`."
                    )
                );
            }
            if (ranges.Take(i).Any(other => range.Start <= other.End && other.Start <= range.End))
            {
                diagnostics.Add(new DiagnosticRecord("ACTA0204", $"`{enumName}` declares overlapping reserved ranges."));
            }
        }
    }

    internal static bool IsValidCodeShape(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return false;
        }

        var segments = code.Split('.');
        foreach (var seg in segments)
        {
            if (seg.Length == 0 || !(seg[0] >= 'a' && seg[0] <= 'z'))
            {
                return false;
            }

            for (var i = 1; i < seg.Length; i++)
            {
                var c = seg[i];
                var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
