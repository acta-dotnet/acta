using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Acta.Generators.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Acta.Generators.Codes;

/// <summary>
/// Emits a <c>{Enum}Extensions</c> companion for every <c>[CodeKind]</c>-bearing enum,
/// surfacing the per-value metadata declared via <c>[Code]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CodeGenerator : IIncrementalGenerator
{
    private const string CodeAttributeMetadataName = "Acta.CodeAttribute";
    private const string CodeKindAttributeMetadataName = "Acta.CodeKindAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var fieldHits = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                CodeAttributeMetadataName,
                predicate: static (node, _) => node is EnumMemberDeclarationSyntax,
                transform: static (ctx, ct) => TransformField(ctx, ct)
            )
            .Where(static f => f is not null)
            .Select(static (f, _) => f!);

        var declaredEnums = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                CodeKindAttributeMetadataName,
                predicate: static (node, _) => node is EnumDeclarationSyntax,
                transform: static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol
            )
            .Collect();

        var grouped = fieldHits
            .Collect()
            .Combine(declaredEnums)
            .SelectMany(static (pair, _) => GroupByContainingEnum(pair.Left, pair.Right));

        context.RegisterSourceOutput(grouped, static (spc, family) => Emit(spc, family));
        context.RegisterSourceOutput(grouped.Collect(), static (spc, families) => EmitMarkdownReport(spc, families));
    }

    private static FieldHit? TransformField(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IFieldSymbol field)
        {
            return null;
        }

        if (field.ContainingType is not INamedTypeSymbol enumType || enumType.TypeKind != TypeKind.Enum)
        {
            return null;
        }

        if (!field.HasConstantValue)
        {
            return null;
        }

        var attr = ctx.Attributes.FirstOrDefault();
        if (attr is null)
        {
            return null;
        }

        if (attr.ConstructorArguments.Length < 2)
        {
            return null;
        }

        var code = attr.ConstructorArguments[0].Value as string ?? "";
        var description = attr.ConstructorArguments[1].Value as string ?? "";

        var lifecycle = "Active";
        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Lifecycle" when named.Value.Value is not null:
                    lifecycle = Convert.ToInt32(named.Value.Value, CultureInfo.InvariantCulture) switch
                    {
                        2 => "Deprecated",
                        3 => "Retired",
                        _ => "Active",
                    };
                    break;
            }
        }

        short id;
        try
        {
            id = Convert.ToInt16(field.ConstantValue, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return new FieldHit(enumType, field.Name, 0, code, description, lifecycle, OutOfRange: true);
        }

        return new FieldHit(enumType, field.Name, id, code, description, lifecycle, OutOfRange: false);
    }

    private static IEnumerable<FamilyModel> GroupByContainingEnum(
        ImmutableArray<FieldHit> hits,
        ImmutableArray<INamedTypeSymbol> declaredEnums
    )
    {
        var enumTypes = new List<INamedTypeSymbol>();
        foreach (var enumType in declaredEnums.Concat(hits.Select(h => h.EnumType)))
        {
            if (!enumTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, enumType)))
            {
                enumTypes.Add(enumType);
            }
        }

        foreach (var enumType in enumTypes.OrderBy(e => e.ToDisplayString(), StringComparer.Ordinal))
        {
            var familyHits = hits.Where(h => SymbolEqualityComparer.Default.Equals(h.EnumType, enumType));
            var ns = enumType.ContainingNamespace.IsGlobalNamespace ? null : enumType.ContainingNamespace.ToDisplayString();
            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticRecord>();

            // [CodeKind("kebab-name")] is required on every [Code]-bearing enum. Every
            // persisted name in Acta is explicit on its attribute; the catalog discriminator is no
            // exception. The generator derives nothing from the C# enum name: that decoupling
            // protects operator queries against silent C# renames (see proposal 0008).
            var codeKindAttr = enumType.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CodeKindAttribute");
            string codeKind;
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
                codeKind = enumType.Name; // Placeholder: emission skips on diagnostic anyway.
            }
            else if (!IsValidCodeShape(declaredKind))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0201",
                        $"`[CodeKind(\"{declaredKind}\")]` on `{enumType.Name}` must match kebab "
                            + $"(`[a-z][a-z0-9-]*`) optionally dotted (`.<kebab>`).",
                        Fatal: true
                    )
                );
                codeKind = declaredKind;
            }
            else
            {
                codeKind = declaredKind;
            }

            // Extensible families emit no IN-list CHECK and read unknown ids back as Unspecified = 0
            // instead of throwing, so new members ship without a migration.
            var extensible = codeKindAttr?.NamedArguments.FirstOrDefault(a => a.Key == "Extensible").Value.Value is true;

            var underlying = enumType.EnumUnderlyingType?.SpecialType ?? SpecialType.None;
            var storage = underlying == SpecialType.System_Byte ? StorageKind.Byte : StorageKind.Invalid;

            if (storage == StorageKind.Invalid)
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0201",
                        $"Persisted code family `{enumType.Name}` must use the `byte` underlying type.",
                        Fatal: true
                    )
                );
                yield return new FamilyModel(
                    ns,
                    enumType.Name,
                    codeKind,
                    extensible,
                    StorageKind.Byte,
                    [],
                    [],
                    [],
                    diagnostics.ToImmutable()
                );
                continue;
            }

            var reservations = ReadReservations(enumType, diagnostics);
            var reservedRanges = ReadReservedRanges(enumType, diagnostics);

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

            ValidateReservations(enumType.Name, codes, reservations, reservedRanges, diagnostics);

            if (extensible && !codes.Any(c => c.Id == 0))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0201",
                        $"`[CodeKind(Extensible = true)]` on `{enumType.Name}` requires a member with id 0 "
                            + $"(by convention `Unspecified`), which unknown persisted ids read back as.",
                        Fatal: true
                    )
                );
            }

            codes.Sort((a, b) => a.Id.CompareTo(b.Id));

            yield return new FamilyModel(
                ns,
                enumType.Name,
                codeKind,
                extensible,
                storage,
                [.. codes],
                reservations,
                reservedRanges,
                diagnostics.ToImmutable()
            );
        }
    }

    private static ImmutableArray<ReservationModel> ReadReservations(
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

    private static ImmutableArray<ReservedRangeModel> ReadReservedRanges(
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
            var permanent = attr.NamedArguments.Any(a =>
                a.Key == "PermanentlyUnavailable"
                && a.Value.Value is not null
                && Convert.ToBoolean(a.Value.Value, CultureInfo.InvariantCulture)
            );
            if (start < 0 || end > 254 || start > end)
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0204",
                        $"`{enumType.Name}` reserved range `{start}..{end}` must be ordered and contained in 0..254."
                    )
                );
            }
            ranges.Add(new ReservedRangeModel(start, end, permanent));
        }
        return ranges.ToImmutable();
    }

    private static void ValidateReservations(
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

    private static bool IsValidCodeShape(string code)
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

    // ACTA02xx diagnostics. Messages are fully formatted at the check site; every descriptor passes
    // them through. Static descriptors keep the IDs discoverable for analyzer release tracking (RS2002).
    private static readonly DiagnosticDescriptor CodeFamilyDeclaration = new(
        id: "ACTA0201",
        title: "Code-family declarations must be complete",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor CodeValue = new(
        id: "ACTA0202",
        title: "Code values must be well-formed",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor DuplicateCodeValue = new(
        id: "ACTA0203",
        title: "Code values must be unique within a family",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor ReservedCodeValue = new(
        id: "ACTA0204",
        title: "Retired and reserved code identities cannot be reused",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static DiagnosticDescriptor DescriptorFor(string id) =>
        id switch
        {
            "ACTA0201" => CodeFamilyDeclaration,
            "ACTA0202" => CodeValue,
            "ACTA0203" => DuplicateCodeValue,
            "ACTA0204" => ReservedCodeValue,
            _ => throw new InvalidOperationException($"Unknown ACTA02xx descriptor '{id}'."),
        };

    private static void Emit(SourceProductionContext spc, FamilyModel family)
    {
        foreach (var diag in family.Diagnostics)
        {
            spc.ReportDiagnostic(Diagnostic.Create(DescriptorFor(diag.Id), Location.None, diag.Message));
        }
        if (family.Diagnostics.Any(d => d.Fatal))
        {
            return;
        }

        EmitExtensions(spc, family);
    }

    private static void EmitExtensions(SourceProductionContext spc, FamilyModel family)
    {
        var storageName = StorageKeyword(family.Storage);
        var typeName = family.StructName;
        var extName = $"{typeName}Extensions";
        var assignedCount = family.Codes.Count(c => c.Lifecycle == "Active");
        var deprecatedCount = family.Codes.Count(c => c.Lifecycle == "Deprecated");
        var retiredCount = family.Codes.Count(c => c.Lifecycle == "Retired") + family.Reservations.Length;
        var permanentlyReservedCount = family
            .ReservedRanges.Where(r => r.PermanentlyUnavailable && r.Start <= r.End)
            .Sum(r => r.End - r.Start + 1);
        var heldReserveCount = family
            .ReservedRanges.Where(r => !r.PermanentlyUnavailable && r.Start <= r.End)
            .Sum(r => r.End - r.Start + 1);
        var zeroUsable =
            family.Codes.Any(c => c.Id == 0) || family.Reservations.Any(r => r.Id == 0) || family.ReservedRanges.Any(r => r.Start == 0);
        var invalidSentinelCount = zeroUsable ? 1 : 2;
        var usableCapacity = zeroUsable ? 255 : 254;
        var availableCount = Math.Max(
            0,
            usableCapacity - assignedCount - deprecatedCount - retiredCount - permanentlyReservedCount - heldReserveCount
        );

        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine();
        if (family.ContainingNamespace is not null)
        {
            sb.Append("namespace ").Append(family.ContainingNamespace).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append("public static partial class ").AppendLine(extName);
        sb.AppendLine("{");

        sb.AppendLine(
            "    private static readonly global::System.Collections.Generic.IReadOnlyList<global::Acta.CodeManifestEntry> _manifest = new global::Acta.CodeManifestEntry[]"
        );
        sb.AppendLine("    {");
        foreach (var c in family.Codes)
        {
            sb.Append("        new(\"")
                .Append(family.CodeKind)
                .Append("\", (byte)")
                .Append(c.Id)
                .Append(", \"")
                .Append(c.Code)
                .Append("\", ")
                .Append(EscapeStringLiteral(c.Description))
                .Append(", global::Acta.CodeLifecycle.")
                .Append(c.Lifecycle)
                .AppendLine("),");
        }
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.Append("    extension(").Append(typeName).AppendLine(")");
        sb.AppendLine("    {");
        sb.Append("        public static string CodeKind => \"").Append(family.CodeKind).AppendLine("\";");
        sb.AppendLine();
        sb.AppendLine(
            "        public static global::System.Collections.Generic.IReadOnlyList<global::Acta.CodeManifestEntry> Manifest => _manifest;"
        );
        sb.AppendLine();
        sb.Append("        public static global::Acta.CodeCapacityReport Capacity => new(")
            .Append(assignedCount)
            .Append(", ")
            .Append(deprecatedCount)
            .Append(", ")
            .Append(retiredCount)
            .Append(", ")
            .Append(permanentlyReservedCount)
            .Append(", ")
            .Append(heldReserveCount)
            .Append(", ")
            .Append(availableCount)
            .Append(", ")
            .Append(invalidSentinelCount)
            .AppendLine(");");
        sb.AppendLine();

        if (family.Codes.Length == 0)
        {
            sb.Append("        public static bool IsKnownId(").Append(storageName).AppendLine(" id) => false;");
        }
        else
        {
            sb.Append("        public static bool IsKnownId(").Append(storageName).Append(" id) => id is ");
            sb.Append(string.Join(" or ", family.Codes.Select(c => c.Id.ToString(CultureInfo.InvariantCulture))));
            sb.AppendLine(";");
        }
        sb.AppendLine();

        sb.Append("        public static bool IsWritableId(").Append(storageName).AppendLine(" id) => id switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            ").Append(c.Id).Append(" => ").Append(c.Lifecycle == "Retired" ? "false" : "true").AppendLine(",");
        }

        sb.AppendLine("            _ => false,");
        sb.AppendLine("        };");
        sb.AppendLine();

        sb.Append("        public static ").Append(typeName).Append(" FromId(").Append(storageName).AppendLine(" id) => id switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            ").Append(c.Id).Append(" => ").Append(typeName).Append('.').Append(c.MemberName).AppendLine(",");
        }

        // Extensible families tolerate ids this build does not know: the row was written by a newer
        // Acta, and a forward read must not throw. FromCode stays strict either way - it parses caller
        // input, where an unrecognized code is a bad request, not a version gap.
        if (family.Extensible)
        {
            // Whichever member holds id 0, not a hardcoded name: the ACTA0201 guard requires id 0 to
            // exist but does not dictate what it is called.
            var fallback = family.Codes.First(c => c.Id == 0).MemberName;
            sb.Append("            _ => ").Append(typeName).Append('.').Append(fallback).AppendLine(",");
        }
        else
        {
            sb.Append("            _ => throw new global::System.ArgumentOutOfRangeException(nameof(id), id, \"Unknown ")
                .Append(typeName)
                .AppendLine(" id.\"),");
        }

        sb.AppendLine("        };");
        sb.AppendLine();

        sb.Append("        public static ").Append(typeName).AppendLine(" FromCode(string code) => code switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            \"").Append(c.Code).Append("\" => ").Append(typeName).Append('.').Append(c.MemberName).AppendLine(",");
        }
        sb.Append("            _ => throw new global::System.ArgumentOutOfRangeException(nameof(code), code, \"Unknown ")
            .Append(typeName)
            .AppendLine(" code.\"),");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.Append("    extension(").Append(typeName).AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        public byte ToId => value switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            ").Append(typeName).Append('.').Append(c.MemberName).Append(" => ").Append(c.Id).AppendLine(",");
        }
        sb.Append("            _ => throw new global::System.ArgumentOutOfRangeException(nameof(value), value, \"Unknown ")
            .Append(typeName)
            .AppendLine(" value.\"),");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        public string Code => value switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            ").Append(typeName).Append('.').Append(c.MemberName).Append(" => \"").Append(c.Code).AppendLine("\",");
        }

        sb.Append("            _ => throw new global::System.ArgumentOutOfRangeException(nameof(value), value, \"Unknown ")
            .Append(typeName)
            .AppendLine(" value.\"),");
        sb.AppendLine("        };");
        sb.AppendLine();
        sb.AppendLine("        public string Description => value switch");
        sb.AppendLine("        {");
        foreach (var c in family.Codes)
        {
            sb.Append("            ")
                .Append(typeName)
                .Append('.')
                .Append(c.MemberName)
                .Append(" => ")
                .Append(EscapeStringLiteral(c.Description))
                .AppendLine(",");
        }

        sb.Append("            _ => throw new global::System.ArgumentOutOfRangeException(nameof(value), value, \"Unknown ")
            .Append(typeName)
            .AppendLine(" value.\"),");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        AppendJsonConverter(sb, family, typeName, storageName);

        spc.AddSource($"{extName}.g.cs", sb.ToString());
    }

    private static void AppendJsonConverter(StringBuilder sb, FamilyModel family, string typeName, string storageName)
    {
        var readerNumeric = storageName switch
        {
            "byte" => "reader.GetByte()",
            "short" => "reader.GetInt16()",
            "int" => "reader.GetInt32()",
            _ => throw new InvalidOperationException($"Unsupported code storage '{storageName}' on {typeName}."),
        };
        var parseNumeric = storageName switch
        {
            "byte" => "byte.TryParse",
            "short" => "short.TryParse",
            "int" => "int.TryParse",
            _ => throw new InvalidOperationException($"Unsupported code storage '{storageName}' on {typeName}."),
        };

        sb.AppendLine();
        sb.Append("/// <summary>Reads / writes <see cref=\"")
            .Append(typeName)
            .AppendLine("\"/> as its kebab <c>Code</c> string; accepts numeric values as a fallback.</summary>");
        sb.Append("public sealed class ")
            .Append(typeName)
            .Append("JsonConverter : global::System.Text.Json.Serialization.JsonConverter<")
            .Append(typeName)
            .AppendLine(">");
        sb.AppendLine("{");
        sb.Append("    public override ")
            .Append(typeName)
            .AppendLine(
                " Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)"
            );
        sb.AppendLine("    {");
        sb.AppendLine("        switch (reader.TokenType)");
        sb.AppendLine("        {");
        sb.AppendLine("            case global::System.Text.Json.JsonTokenType.String:");
        sb.AppendLine("                var s = reader.GetString();");
        sb.AppendLine("                return s switch");
        sb.AppendLine("                {");
        foreach (var c in family.Codes)
        {
            sb.Append("                    \"")
                .Append(c.Code)
                .Append("\" => ")
                .Append(typeName)
                .Append('.')
                .Append(c.MemberName)
                .AppendLine(",");
        }

        sb.Append("                    _ when s is not null && ")
            .Append(parseNumeric)
            .Append(
                "(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var n) => "
            )
            .Append(typeName)
            .AppendLine(".FromId(n),");
        sb.Append("                    _ => throw new global::System.Text.Json.JsonException($\"Unknown ")
            .Append(typeName)
            .AppendLine(" code: '{s}'.\"),");
        sb.AppendLine("                };");
        sb.Append("            case global::System.Text.Json.JsonTokenType.Number: return ")
            .Append(typeName)
            .Append(".FromId(")
            .Append(readerNumeric)
            .AppendLine(");");
        sb.Append("            default: throw new global::System.Text.Json.JsonException(\"")
            .Append(typeName)
            .AppendLine(" expects a JSON string or number.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append("    public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(typeName)
            .AppendLine(" value, global::System.Text.Json.JsonSerializerOptions options)");
        sb.AppendLine("        => writer.WriteStringValue(value.Code);");
        sb.AppendLine("}");
    }

    private static void EmitMarkdownReport(SourceProductionContext spc, ImmutableArray<FamilyModel> families)
    {
        var validFamilies = families
            .Where(f => !f.Diagnostics.Any(d => d.Fatal))
            .OrderBy(f => f.CodeKind, StringComparer.Ordinal)
            .ToArray();
        if (validFamilies.Length == 0)
        {
            return;
        }

        var md = new StringBuilder();
        md.AppendLine("# 98 · Codes (mechanical reference)");
        md.AppendLine();
        md.AppendLine("Auto-generated from `[Code]` decorations on each source-generated code family in `Acta` domain folders.");
        md.AppendLine("Edit the attributes, not this file. Regenerate via `dotnet run --project tools/Acta.Emit -- docs`.");
        md.AppendLine();
        md.AppendLine($"Total families: **{validFamilies.Length}**. Total codes: **{validFamilies.Sum(f => f.Codes.Length)}**.");
        md.AppendLine();

        foreach (var f in validFamilies)
        {
            var storageName = StorageKeyword(f.Storage);
            md.Append("## `")
                .Append(f.StructName)
                .Append("` · `")
                .Append(f.CodeKind)
                .Append("` (")
                .Append(storageName)
                .AppendLine("-backed)");
            md.AppendLine();
            md.AppendLine("| Id  | Code | Description | Lifecycle |");
            md.AppendLine("|----:|------|-------------|-----------|");
            foreach (var c in f.Codes.OrderBy(c => c.Id))
            {
                md.Append("| ").Append(c.Id);
                md.Append(" | `").Append(EscapeMd(c.Code)).Append('`');
                md.Append(" | ").Append(EscapeMd(c.Description));
                md.Append(" | ").Append(c.Lifecycle).AppendLine(" |");
            }
            md.AppendLine();
        }

        var src = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(src);
        src.AppendLine();
        src.AppendLine("namespace Acta;");
        src.AppendLine();
        src.AppendLine("public static partial class CodeManifests");
        src.AppendLine("{");
        src.AppendLine(
            "    private static readonly global::System.Collections.Generic.IReadOnlyList<global::Acta.CodeManifestEntry> _all = new global::Acta.CodeManifestEntry[]"
        );
        src.AppendLine("    {");
        foreach (var f in validFamilies)
        {
            foreach (var c in f.Codes)
            {
                src.Append("        new(\"")
                    .Append(f.CodeKind)
                    .Append("\", (byte)")
                    .Append(c.Id)
                    .Append(", \"")
                    .Append(c.Code)
                    .Append("\", ")
                    .Append(EscapeStringLiteral(c.Description))
                    .Append(", global::Acta.CodeLifecycle.")
                    .Append(c.Lifecycle)
                    .AppendLine("),");
            }
        }
        src.AppendLine("    };");
        src.AppendLine();
        src.AppendLine("    /// <summary>Mechanical manifest entries for every generated code family.</summary>");
        src.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<global::Acta.CodeManifestEntry> All => _all;");
        src.AppendLine();
        src.AppendLine(
            "    /// <summary>Mechanical markdown reference for every code family, generated from `[Code]` decorations.</summary>"
        );
        src.AppendLine("    public const string MarkdownReport = ");

        var lines = md.ToString().Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            src.Append("        ").Append(EscapeStringLiteral(lines[i]));
            if (i < lines.Length - 1)
            {
                src.AppendLine(" + \"\\n\" +");
            }
            else
            {
                src.AppendLine(";");
            }
        }
        src.AppendLine("}");

        spc.AddSource("CodeManifests.MarkdownReport.g.cs", src.ToString());
    }

    private static string EscapeMd(string s) => s.Replace("|", "\\|");

    private static string EscapeStringLiteral(string s)
    {
        var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private enum StorageKind
    {
        Byte,
        Short,
        Int,
        Invalid,
    }

    private static string StorageKeyword(StorageKind storage) =>
        storage switch
        {
            StorageKind.Byte => "byte",
            StorageKind.Short => "short",
            StorageKind.Int => "int",
            _ => throw new InvalidOperationException($"Cannot render StorageKind.{storage} as a C# keyword."),
        };

    // Fatal marks family-level breakage that suppresses emission for the whole family.
    private sealed record DiagnosticRecord(string Id, string Message, bool Fatal = false);

    private sealed record FieldHit(
        INamedTypeSymbol EnumType,
        string MemberName,
        short Id,
        string Code,
        string Description,
        string Lifecycle,
        bool OutOfRange
    );

    private sealed record FamilyModel(
        string? ContainingNamespace,
        string StructName,
        string CodeKind,
        bool Extensible,
        StorageKind Storage,
        ImmutableArray<CodeModel> Codes,
        ImmutableArray<ReservationModel> Reservations,
        ImmutableArray<ReservedRangeModel> ReservedRanges,
        ImmutableArray<DiagnosticRecord> Diagnostics
    );

    private sealed record CodeModel(string MemberName, short Id, string Code, string Description, string Lifecycle);

    private sealed record ReservationModel(short Id, string Code);

    private sealed record ReservedRangeModel(short Start, short End, bool PermanentlyUnavailable);
}
