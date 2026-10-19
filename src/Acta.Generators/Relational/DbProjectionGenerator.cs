using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Acta.Generators.ProjectionReader;
using static Acta.Generators.ProjectionSourceWriter;

namespace Acta.Generators;

/// <summary>
/// Emits <c>DbDataReader</c> binders: for each type marked <c>[DbProjection]</c>, by position unless it
/// asks for <c>ByName</c>, and for every other type the code passes to <c>DbProjectionResolver.Resolve&lt;T&gt;()</c>,
/// by column name. The call is the declaration, so a public model in <c>Acta</c> binds without an attribute.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DbProjectionGenerator : IIncrementalGenerator
{
    internal const string DbProjectionAttr = "Acta.DbProjectionAttribute";
    internal const string ResolverMethodName = "__ActaTryResolveDbProjection";

    internal static readonly DiagnosticDescriptor ProjectionDeclaration = new(
        id: "ACTA0501",
        title: "Projection declarations must be materializable",
        messageFormat: "{0}",
        category: "ActaProjection",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var declaredProjections = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                DbProjectionAttr,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static p => p is not null)
            .Select(static (p, _) => p!.Value);

        var resolvedProjections = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => IsResolveCall(node),
                transform: static (ctx, ct) => TransformResolved(ctx, ct)
            )
            .Where(static p => p is not null)
            .Select(static (p, _) => p!.Value);

        // A declared type keeps its declared binding when the code also resolves it: declarations come first.
        var projections = declaredProjections
            .Collect()
            .Combine(resolvedProjections.Collect())
            .Select(
                static (pair, _) =>
                    pair
                        .Left.AddRange(pair.Right)
                        .GroupBy(static p => p.TypeFqn, StringComparer.Ordinal)
                        .Select(static g => g.First())
                        .ToImmutableArray()
            );

        context.RegisterSourceOutput(
            projections,
            static (spc, items) =>
            {
                foreach (var diagnostic in items.SelectMany(static p => p.Diagnostics))
                {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }

                Emit(spc, items);
            }
        );
    }

    internal static void Emit(SourceProductionContext spc, ImmutableArray<ProjectionInfo> projections)
    {
        var emittable = projections.Where(static p => p.Diagnostics.IsDefaultOrEmpty).ToArray();
        if (emittable.Length == 0)
        {
            return;
        }

        foreach (var group in emittable.GroupBy(static p => p.NamespaceName))
        {
            var ordered = group.OrderBy(static p => p.MethodName, StringComparer.Ordinal).ToImmutableArray();
            spc.AddSource($"{HintPart(group.Key)}.DbProjectionBinder.g.cs", EmitNamespaceBinder(group.Key, ordered));
        }

        spc.AddSource("Acta.DbProjectionResolver.g.cs", EmitResolver([.. emittable]));
    }

    internal readonly record struct ProjectionInfo(
        string NamespaceName,
        string TypeName,
        string TypeFqn,
        string MethodName,
        bool ByName,
        ImmutableArray<ProjectionMemberInfo> Members,
        ImmutableArray<DiagnosticInfo> Diagnostics
    )
    {
        public string ColumnsName => TypeName + "Columns";
    }

    internal readonly record struct ProjectionMemberInfo(
        string Name,
        bool IsNullable,
        ProjectionMemberKind Kind,
        string? EnumTypeFqn,
        string? DecoderFqn,
        string? RefTypeFqn
    )
    {
        /// <summary>The column the member reads: its name in snake_case.</summary>
        public string Column => SnakeCase(Name);

        internal static string SnakeCase(string name)
        {
            var sb = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && i > 0)
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            return sb.ToString();
        }

        public static ProjectionMemberInfo? Create(
            IParameterSymbol parameter,
            LocationInfo location,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics
        )
        {
            var (isNullable, nonNullableType) = AnalyzeType(parameter.Type);
            string? enumTypeFqn = null;
            string? decoderFqn = null;
            var kind = KindOf(nonNullableType);

            if (nonNullableType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                enumTypeFqn = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                decoderFqn = CodeFamilyNames.DecoderFqn(enumType);
                kind =
                    enumType.EnumUnderlyingType?.SpecialType == SpecialType.System_Byte
                        ? ProjectionMemberKind.Byte
                        : ProjectionMemberKind.Unsupported;
            }

            if (kind == ProjectionMemberKind.Unsupported)
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ProjectionDeclaration.Id,
                        location,
                        $"[DbProjection] parameter '{parameter.Name}' has unsupported type '{parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}'."
                    )
                );
                return null;
            }

            var refTypeFqn =
                kind == ProjectionMemberKind.Ref ? nonNullableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
            return new ProjectionMemberInfo(parameter.Name, isNullable, kind, enumTypeFqn, decoderFqn, refTypeFqn);
        }

        internal static (bool IsNullable, ITypeSymbol NonNullableType) AnalyzeType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol nt && nt.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return (true, nt.TypeArguments[0]);
            }

            if (type.IsValueType)
            {
                return (false, type);
            }

            return (type.NullableAnnotation == NullableAnnotation.Annotated, type);
        }

        internal static ProjectionMemberKind KindOf(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                return ProjectionMemberKind.Boolean;
            }
            if (type.SpecialType == SpecialType.System_Byte)
            {
                return ProjectionMemberKind.Byte;
            }
            if (type.SpecialType == SpecialType.System_Int32)
            {
                return ProjectionMemberKind.Int32;
            }
            if (type.SpecialType == SpecialType.System_Int64)
            {
                return ProjectionMemberKind.Int64;
            }
            if (type.SpecialType == SpecialType.System_String)
            {
                return ProjectionMemberKind.String;
            }
            if (type.ToDisplayString() == "System.Guid")
            {
                return ProjectionMemberKind.Guid;
            }
            if (type.ToDisplayString() == "System.DateTime")
            {
                return ProjectionMemberKind.UtcInstant;
            }
            if (IsRef(type))
            {
                return ProjectionMemberKind.Ref;
            }
            if (IsByteArray(type))
            {
                return ProjectionMemberKind.Bytes;
            }
            return IsNamedGeneric(type, "System.ReadOnlyMemory<T>") && IsByteType(((INamedTypeSymbol)type).TypeArguments[0])
                ? ProjectionMemberKind.ReadOnlyMemoryBytes
                : ProjectionMemberKind.Unsupported;
        }

        // A ref is a struct wrapping one Guid: JobRef, AlertRef, WorkerRef.
        internal static bool IsRef(ITypeSymbol type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Struct } named
            && named.InstanceConstructors.Any(static c =>
                c.Parameters.Length == 1
                && c.Parameters[0].Type.ToDisplayString() == "System.Guid"
                && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            );

        internal static bool IsByteArray(ITypeSymbol type) => type is IArrayTypeSymbol array && IsByteType(array.ElementType);

        internal static bool IsNamedGeneric(ITypeSymbol type, string metadataName) =>
            type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == metadataName;

        internal static bool IsByteType(ITypeSymbol type) => type.SpecialType == SpecialType.System_Byte;
    }

    internal enum ProjectionMemberKind
    {
        Unsupported,
        Boolean,
        Byte,
        Int32,
        Int64,
        Guid,
        Ref,
        UtcInstant,
        String,
        Bytes,
        ReadOnlyMemoryBytes,
    }

    internal readonly record struct DiagnosticInfo(string DescriptorId, LocationInfo Location, string Message)
    {
        public Diagnostic ToDiagnostic()
        {
            var descriptor = DescriptorId switch
            {
                "ACTA0501" => ProjectionDeclaration,
                _ => throw new InvalidOperationException($"Unknown ACTA05xx descriptor '{DescriptorId}'."),
            };
            return Diagnostic.Create(descriptor, Location.ToLocation(), Message);
        }
    }

    internal readonly record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var line = location.GetLineSpan();
            return new LocationInfo(line.Path, location.SourceSpan, line.Span);
        }

        public Location ToLocation() => string.IsNullOrEmpty(FilePath) ? Location.None : Location.Create(FilePath, TextSpan, LineSpan);
    }
}
