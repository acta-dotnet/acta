using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Acta.Generators.CodeFamilyReader;
using static Acta.Generators.CodeSourceWriter;

namespace Acta.Generators;

/// <summary>
/// Emits a <c>{Enum}Extensions</c> companion for every <c>[CodeKind]</c>-bearing enum,
/// surfacing the per-value metadata declared via <c>[Code]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CodeGenerator : IIncrementalGenerator
{
    internal const string CodeAttributeMetadataName = "Acta.CodeAttribute";
    internal const string CodeKindAttributeMetadataName = "Acta.CodeKindAttribute";

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

    internal static FieldHit? TransformField(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
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

    internal static IEnumerable<FamilyModel> GroupByContainingEnum(
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

            var codeKind = ReadCodeKind(enumType, diagnostics);

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
                yield return new FamilyModel(ns, enumType.Name, codeKind, StorageKind.Byte, [], diagnostics.ToImmutable());
                continue;
            }

            var reservations = ReadReservations(enumType, diagnostics);
            var reservedRanges = ReadReservedRanges(enumType, diagnostics);

            var codes = ReadCodes(enumType, familyHits, diagnostics);

            ValidateReservations(enumType.Name, codes, reservations, reservedRanges, diagnostics);

            // Every family decodes an id it does not know as its id-0 member, so a row a newer Acta wrote
            // reads back during a rolling deploy: id 0 is the family's default or Unspecified.
            if (codes.Count > 0 && !codes.Any(c => c.Id == 0))
            {
                diagnostics.Add(
                    new DiagnosticRecord(
                        "ACTA0201",
                        $"`{enumType.Name}` requires a member with id 0, the family's default or `Unspecified`, "
                            + $"which unknown persisted ids read back as.",
                        Fatal: true
                    )
                );
            }

            codes.Sort((a, b) => a.Id.CompareTo(b.Id));

            yield return new FamilyModel(ns, enumType.Name, codeKind, storage, [.. codes], diagnostics.ToImmutable());
        }
    }

    /// <summary>
    /// ACTA02xx diagnostics. Messages are fully formatted at the check site; every descriptor passes
    /// them through. Static descriptors keep the IDs discoverable for analyzer release tracking (RS2002).
    /// </summary>
    internal static readonly DiagnosticDescriptor CodeFamilyDeclaration = new(
        id: "ACTA0201",
        title: "Code-family declarations must be complete",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor CodeValue = new(
        id: "ACTA0202",
        title: "Code values must be well-formed",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor DuplicateCodeValue = new(
        id: "ACTA0203",
        title: "Code values must be unique within a family",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static readonly DiagnosticDescriptor ReservedCodeValue = new(
        id: "ACTA0204",
        title: "Retired and reserved code identities cannot be reused",
        messageFormat: "{0}",
        category: "ActaCodes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    internal static DiagnosticDescriptor DescriptorFor(string id) =>
        id switch
        {
            "ACTA0201" => CodeFamilyDeclaration,
            "ACTA0202" => CodeValue,
            "ACTA0203" => DuplicateCodeValue,
            "ACTA0204" => ReservedCodeValue,
            _ => throw new InvalidOperationException($"Unknown ACTA02xx descriptor '{id}'."),
        };

    /// <summary>
    /// Byte is the only legal storage: ACTA0201 rejects any other underlying type, so Invalid exists
    /// only to carry that rejection to the diagnostic and never reaches emission.
    /// </summary>
    internal enum StorageKind
    {
        Byte,
        Invalid,
    }

    /// <summary>
    /// Fatal marks family-level breakage that suppresses emission for the whole family.
    /// </summary>
    internal sealed record DiagnosticRecord(string Id, string Message, bool Fatal = false);

    internal sealed record FieldHit(
        INamedTypeSymbol EnumType,
        string MemberName,
        short Id,
        string Code,
        string Description,
        string Lifecycle,
        bool OutOfRange
    );

    internal sealed record FamilyModel(
        string? ContainingNamespace,
        string StructName,
        string CodeKind,
        StorageKind Storage,
        ImmutableArray<CodeModel> Codes,
        ImmutableArray<DiagnosticRecord> Diagnostics
    );

    internal sealed record CodeModel(string MemberName, short Id, string Code, string Description, string Lifecycle);

    internal sealed record ReservationModel(short Id, string Code);

    /// <summary>
    /// Start/End only: the range's whole machine effect is the ACTA0204 assignment gate, which treats
    /// every held id alike. PermanentlyUnavailable on the attribute records intent for readers.
    /// </summary>
    internal sealed record ReservedRangeModel(short Start, short End);
}
