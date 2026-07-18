using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Acta.Generators.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Acta.Generators.Relational;

/// <summary>
/// Emits ordinal-only <c>DbDataReader</c> binders for provider result-row types marked with
/// <c>[DbProjection]</c>. The marker carries no SQL meaning; provider stores own selection order.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DbProjectionGenerator : IIncrementalGenerator
{
    private const string DbProjectionAttr = "Acta.Relational.Commands.DbProjectionAttribute";
    private const string ResolverMethodName = "__ActaTryResolveDbProjection";

    private static readonly DiagnosticDescriptor ProjectionDeclaration = new(
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
                transform: static (ctx, ct) => Transform(ctx, ct)
            )
            .Where(static p => p is not null)
            .Select(static (p, _) => p!.Value);

        var referencedProjections = context.CompilationProvider.Select(
            static (compilation, ct) => TransformAssemblyProjections(compilation, ct)
        );

        var projections = declaredProjections
            .Collect()
            .Combine(referencedProjections)
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

    private static ProjectionInfo? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        return Transform(type, ct);
    }

    private static ImmutableArray<ProjectionInfo> TransformAssemblyProjections(Compilation compilation, CancellationToken ct)
    {
        var projections = ImmutableArray.CreateBuilder<ProjectionInfo>();
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (!string.Equals(attribute.AttributeClass?.ToDisplayString(), DbProjectionAttr, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                var values = argument.Kind == TypedConstantKind.Array ? argument.Values : ImmutableArray.Create(argument);
                foreach (var value in values)
                {
                    if (value.Value is INamedTypeSymbol type)
                    {
                        projections.Add(Transform(type, ct));
                    }
                }
            }
        }

        return projections.ToImmutable();
    }

    private static ProjectionInfo Transform(INamedTypeSymbol type, CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var typeLocation = type.Locations.FirstOrDefault();
        var typeLoc = typeLocation is null ? default : LocationInfo.From(typeLocation);

        if (type.IsGenericType)
        {
            diagnostics.Add(new DiagnosticInfo(ProjectionDeclaration.Id, typeLoc, $"[DbProjection] type '{type.Name}' cannot be generic."));
        }

        var containers = ImmutableArray.CreateBuilder<ContainingTypeInfo>();
        var containingTypes = ContainingTypesOf(type).ToArray();
        foreach (var containingType in containingTypes)
        {
            if (containingType.IsGenericType)
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ProjectionDeclaration.Id,
                        typeLoc,
                        $"[DbProjection] nested type '{type.Name}' cannot be generated inside generic containing type '{containingType.Name}'."
                    )
                );
                continue;
            }

            if (!IsPartial(containingType, ct))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ProjectionDeclaration.Id,
                        typeLoc,
                        $"[DbProjection] nested type '{type.Name}' requires containing type '{containingType.Name}' to be partial."
                    )
                );
                continue;
            }

            containers.Add(
                new ContainingTypeInfo(
                    Accessibility: AccessibilityKeyword(containingType.DeclaredAccessibility),
                    Kind: TypeDeclarationKeyword(containingType),
                    Name: containingType.Name,
                    IsStatic: containingType.IsStatic
                )
            );
        }

        var ctor = SelectConstructor(type, diagnostics, typeLoc);
        var members = ImmutableArray.CreateBuilder<ProjectionMemberInfo>();
        if (ctor is not null)
        {
            if (containers.Count == 0 && !IsCallableFromNamespaceBinder(ctor))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ProjectionDeclaration.Id,
                        typeLoc,
                        $"[DbProjection] type '{type.Name}' must expose an internal or public constructor, or be nested in a partial provider-store type."
                    )
                );
            }

            for (var i = 0; i < ctor.Parameters.Length; i++)
            {
                var parameter = ctor.Parameters[i];
                var location = parameter.Locations.FirstOrDefault();
                var paramLoc = location is null ? typeLoc : LocationInfo.From(location);
                var member = ProjectionMemberInfo.Create(parameter, paramLoc, diagnostics);
                if (member is not null)
                {
                    members.Add(member.Value);
                }
            }
        }

        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new ProjectionInfo(
            NamespaceName: ns,
            TypeName: type.Name,
            TypeFqn: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            MethodName: "Bind" + type.Name,
            Containers: containers.ToImmutable(),
            Members: members.ToImmutable(),
            Diagnostics: diagnostics.ToImmutable()
        );
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypesOf(INamedTypeSymbol type)
    {
        var stack = new Stack<INamedTypeSymbol>();
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
        {
            stack.Push(current);
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken ct)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax(ct) is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                return true;
            }
        }

        return false;
    }

    private static IMethodSymbol? SelectConstructor(
        INamedTypeSymbol type,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        LocationInfo typeLoc
    )
    {
        var constructors = type.InstanceConstructors.Where(c => !IsCopyConstructor(c, type)).ToArray();
        var declaredOrPositional = constructors.Where(c => !c.IsImplicitlyDeclared || c.Parameters.Length > 0).ToArray();
        if (declaredOrPositional.Length == 0 && constructors.Length == 1)
        {
            declaredOrPositional = constructors;
        }

        if (declaredOrPositional.Length == 1)
        {
            return declaredOrPositional[0];
        }

        diagnostics.Add(
            new DiagnosticInfo(
                ProjectionDeclaration.Id,
                typeLoc,
                $"[DbProjection] type '{type.Name}' must have exactly one non-copy constructor; found {declaredOrPositional.Length}."
            )
        );
        return null;
    }

    private static bool IsCopyConstructor(IMethodSymbol ctor, INamedTypeSymbol type) =>
        ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    private static bool IsCallableFromNamespaceBinder(IMethodSymbol ctor) =>
        ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    private static void Emit(SourceProductionContext spc, ImmutableArray<ProjectionInfo> projections)
    {
        var emittable = projections.Where(static p => p.Diagnostics.IsDefaultOrEmpty).ToArray();
        if (emittable.Length == 0)
        {
            return;
        }

        foreach (var group in emittable.Where(static p => p.Containers.IsDefaultOrEmpty).GroupBy(static p => p.NamespaceName))
        {
            var ordered = group.OrderBy(static p => p.MethodName, StringComparer.Ordinal).ToImmutableArray();
            spc.AddSource($"{HintPart(group.Key)}.DbProjectionBinder.g.cs", EmitNamespaceBinder(group.Key, ordered));
        }

        foreach (var group in emittable.Where(static p => !p.Containers.IsDefaultOrEmpty).GroupBy(static p => p.ContainerKey))
        {
            var ordered = group.OrderBy(static p => p.MethodName, StringComparer.Ordinal).ToImmutableArray();
            spc.AddSource($"{HintPart(group.Key)}.DbProjectionBinder.g.cs", EmitNestedBinder(ordered));
        }

        spc.AddSource("Acta.Relational.Commands.DbProjectionResolver.g.cs", EmitResolver(emittable.ToImmutableArray()));
    }

    private static string EmitNamespaceBinder(string namespaceName, ImmutableArray<ProjectionInfo> projections)
    {
        var sb = NewSourceBuilder();
        AppendNamespace(sb, namespaceName);
        sb.AppendLine("internal static partial class DbProjectionBinder");
        sb.AppendLine("{");
        foreach (var projection in projections)
        {
            AppendBindMethod(sb, projection, "    ");
        }
        AppendResolverMethod(sb, projections, "    ", binderPrefix: "");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitNestedBinder(ImmutableArray<ProjectionInfo> projections)
    {
        var first = projections[0];
        var sb = NewSourceBuilder();
        AppendNamespace(sb, first.NamespaceName);

        var indent = "";
        foreach (var container in first.Containers)
        {
            sb.Append(indent);
            if (!string.IsNullOrEmpty(container.Accessibility))
            {
                sb.Append(container.Accessibility).Append(' ');
            }
            if (container.IsStatic)
            {
                sb.Append("static ");
            }
            sb.Append("partial ").Append(container.Kind).Append(' ').Append(container.Name).AppendLine();
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).AppendLine("private static partial class DbProjectionBinder");
        sb.Append(indent).AppendLine("{");
        foreach (var projection in projections.OrderBy(static p => p.MethodName, StringComparer.Ordinal))
        {
            AppendBindMethod(sb, projection, indent + "    ");
        }
        sb.Append(indent).AppendLine("}");
        sb.AppendLine();
        AppendResolverMethod(sb, projections, indent, binderPrefix: "DbProjectionBinder.");

        for (var i = first.Containers.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        return sb.ToString();
    }

    private static string EmitResolver(ImmutableArray<ProjectionInfo> projections)
    {
        var namespaceTargets = projections
            .Where(static p => p.Containers.IsDefaultOrEmpty)
            .GroupBy(static p => p.NamespaceName)
            .Select(static g => string.IsNullOrEmpty(g.Key) ? "global::DbProjectionBinder" : "global::" + g.Key + ".DbProjectionBinder");

        var nestedTargets = projections
            .Where(static p => !p.Containers.IsDefaultOrEmpty)
            .GroupBy(static p => p.ContainerKey)
            .Select(static g => g.First().ContainerFqn);

        var targets = namespaceTargets.Concat(nestedTargets).OrderBy(static t => t, StringComparer.Ordinal).ToArray();

        var sb = NewSourceBuilder();
        sb.AppendLine("namespace Acta.Relational.Commands;");
        sb.AppendLine();
        sb.AppendLine("internal static class DbProjectionResolver");
        sb.AppendLine("{");
        sb.AppendLine("    public static Func<DbDataReader, T> Resolve<T>()");
        sb.AppendLine("    {");
        sb.AppendLine("        Func<DbDataReader, T>? read = null;");
        sb.AppendLine("        TryResolveGenerated(ref read);");
        sb.AppendLine(
            "        return read ?? throw new InvalidOperationException($\"No provider projection binder for '{typeof(T).FullName ?? typeof(T).Name}'.\");"
        );
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void TryResolveGenerated<T>(ref Func<DbDataReader, T>? read)");
        sb.AppendLine("    {");
        foreach (var target in targets)
        {
            sb.Append("        ").Append(target).Append('.').Append(ResolverMethodName).AppendLine("(ref read);");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static StringBuilder NewSourceBuilder()
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Acta.Relational.Commands;");
        sb.AppendLine();
        return sb;
    }

    private static void AppendNamespace(StringBuilder sb, string namespaceName)
    {
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
    }

    private static void AppendBindMethod(StringBuilder sb, ProjectionInfo projection, string indent)
    {
        sb.Append(indent)
            .Append("public static ")
            .Append(projection.TypeFqn)
            .Append(' ')
            .Append(projection.MethodName)
            .AppendLine("(DbDataReader r) =>");

        if (projection.Members.Length == 0)
        {
            sb.Append(indent).AppendLine("    new();");
            sb.AppendLine();
            return;
        }

        sb.Append(indent).AppendLine("    new(");
        for (var i = 0; i < projection.Members.Length; i++)
        {
            var member = projection.Members[i];
            sb.Append(indent).Append("        ").Append(EscapeIdentifier(member.Name)).Append(": ").Append(BindExpression(member, i));
            sb.AppendLine(i == projection.Members.Length - 1 ? "" : ",");
        }
        sb.Append(indent).AppendLine("    );");
        sb.AppendLine();
    }

    private static void AppendResolverMethod(
        StringBuilder sb,
        ImmutableArray<ProjectionInfo> projections,
        string indent,
        string binderPrefix
    )
    {
        sb.Append(indent).Append("internal static void ").Append(ResolverMethodName).AppendLine("<T>(ref Func<DbDataReader, T>? read)");
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    if (read is not null)");
        sb.Append(indent).AppendLine("    {");
        sb.Append(indent).AppendLine("        return;");
        sb.Append(indent).AppendLine("    }");
        sb.AppendLine();

        foreach (var projection in projections.OrderBy(static p => p.MethodName, StringComparer.Ordinal))
        {
            sb.Append(indent).Append("    if (typeof(T) == typeof(").Append(projection.TypeFqn).AppendLine("))");
            sb.Append(indent).AppendLine("    {");
            sb.Append(indent)
                .Append("        read = static r => (T)(object)")
                .Append(binderPrefix)
                .Append(projection.MethodName)
                .AppendLine("(r);");
            sb.Append(indent).AppendLine("        return;");
            sb.Append(indent).AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append(indent).AppendLine("}");
        sb.AppendLine();
    }

    private static string BindExpression(ProjectionMemberInfo member, int ordinal)
    {
        var typed = BindTypedExpression(member, ordinal);
        return member.IsNullable ? "r.IsDBNull(" + ordinal + ") ? null : " + typed : typed;
    }

    private static string BindTypedExpression(ProjectionMemberInfo member, int ordinal)
    {
        var ord = ordinal.ToString(CultureInfo.InvariantCulture);
        var readExpr = member.Kind switch
        {
            ProjectionMemberKind.Boolean => "Convert.ToBoolean(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Byte => "Convert.ToByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.SByte => "Convert.ToSByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int16 => "Convert.ToInt16(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt16 => "Convert.ToUInt16(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int32 => "Convert.ToInt32(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt32 => "Convert.ToUInt32(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int64 => "Convert.ToInt64(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt64 => "Convert.ToUInt64(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Guid => "r.GetGuid(" + ord + ")",
            ProjectionMemberKind.UtcInstant => "r.GetDateTimeUtc(" + ord + ")",
            ProjectionMemberKind.Decimal => "Convert.ToDecimal(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.String => "r.GetString(" + ord + ")",
            ProjectionMemberKind.Bytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.ReadOnlyMemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.MemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            _ => throw new InvalidOperationException($"Unsupported projection member kind {member.Kind}."),
        };

        return member.EnumTypeFqn is null ? readExpr : "(" + member.EnumTypeFqn + ")" + readExpr;
    }

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None
            ? name
            : "@" + name;

    private static string AccessibilityKeyword(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Public => "public",
            _ => "",
        };

    private static string TypeDeclarationKeyword(INamedTypeSymbol type) =>
        type.TypeKind switch
        {
            TypeKind.Struct when type.IsRecord => "record struct",
            TypeKind.Struct => "struct",
            TypeKind.Class when type.IsRecord => "record",
            _ => "class",
        };

    private static string HintPart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Global";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }

    private readonly record struct ProjectionInfo(
        string NamespaceName,
        string TypeName,
        string TypeFqn,
        string MethodName,
        ImmutableArray<ContainingTypeInfo> Containers,
        ImmutableArray<ProjectionMemberInfo> Members,
        ImmutableArray<DiagnosticInfo> Diagnostics
    )
    {
        public string ContainerKey => NamespaceName + "." + string.Join(".", Containers.Select(static c => c.Name));

        public string ContainerFqn
        {
            get
            {
                var prefix = string.IsNullOrEmpty(NamespaceName) ? "global::" : "global::" + NamespaceName + ".";
                return prefix + string.Join(".", Containers.Select(static c => c.Name));
            }
        }
    }

    private readonly record struct ContainingTypeInfo(string Accessibility, string Kind, string Name, bool IsStatic);

    private readonly record struct ProjectionMemberInfo(string Name, bool IsNullable, ProjectionMemberKind Kind, string? EnumTypeFqn)
    {
        public static ProjectionMemberInfo? Create(
            IParameterSymbol parameter,
            LocationInfo location,
            ImmutableArray<DiagnosticInfo>.Builder diagnostics
        )
        {
            var (isNullable, nonNullableType) = AnalyzeType(parameter.Type);
            string? enumTypeFqn = null;
            var kind = KindOf(nonNullableType);

            if (nonNullableType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                enumTypeFqn = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                kind = enumType.EnumUnderlyingType?.SpecialType switch
                {
                    SpecialType.System_Byte => ProjectionMemberKind.Byte,
                    SpecialType.System_SByte => ProjectionMemberKind.SByte,
                    SpecialType.System_Int16 => ProjectionMemberKind.Int16,
                    SpecialType.System_UInt16 => ProjectionMemberKind.UInt16,
                    SpecialType.System_Int32 => ProjectionMemberKind.Int32,
                    SpecialType.System_UInt32 => ProjectionMemberKind.UInt32,
                    SpecialType.System_Int64 => ProjectionMemberKind.Int64,
                    SpecialType.System_UInt64 => ProjectionMemberKind.UInt64,
                    _ => ProjectionMemberKind.Unsupported,
                };
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

            return new ProjectionMemberInfo(parameter.Name, isNullable, kind, enumTypeFqn);
        }

        private static (bool IsNullable, ITypeSymbol NonNullableType) AnalyzeType(ITypeSymbol type)
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

        private static ProjectionMemberKind KindOf(ITypeSymbol type)
        {
            if (type.SpecialType == SpecialType.System_Boolean)
            {
                return ProjectionMemberKind.Boolean;
            }
            if (type.SpecialType == SpecialType.System_Byte)
            {
                return ProjectionMemberKind.Byte;
            }
            if (type.SpecialType == SpecialType.System_SByte)
            {
                return ProjectionMemberKind.SByte;
            }
            if (type.SpecialType == SpecialType.System_Int16)
            {
                return ProjectionMemberKind.Int16;
            }
            if (type.SpecialType == SpecialType.System_UInt16)
            {
                return ProjectionMemberKind.UInt16;
            }
            if (type.SpecialType == SpecialType.System_Int32)
            {
                return ProjectionMemberKind.Int32;
            }
            if (type.SpecialType == SpecialType.System_UInt32)
            {
                return ProjectionMemberKind.UInt32;
            }
            if (type.SpecialType == SpecialType.System_Int64)
            {
                return ProjectionMemberKind.Int64;
            }
            if (type.SpecialType == SpecialType.System_UInt64)
            {
                return ProjectionMemberKind.UInt64;
            }
            if (type.SpecialType == SpecialType.System_Decimal)
            {
                return ProjectionMemberKind.Decimal;
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
            if (IsByteArray(type))
            {
                return ProjectionMemberKind.Bytes;
            }
            if (IsNamedGeneric(type, "System.ReadOnlyMemory<T>") && IsByteType(((INamedTypeSymbol)type).TypeArguments[0]))
            {
                return ProjectionMemberKind.ReadOnlyMemoryBytes;
            }
            if (IsNamedGeneric(type, "System.Memory<T>") && IsByteType(((INamedTypeSymbol)type).TypeArguments[0]))
            {
                return ProjectionMemberKind.MemoryBytes;
            }

            return ProjectionMemberKind.Unsupported;
        }

        private static bool IsByteArray(ITypeSymbol type) => type is IArrayTypeSymbol array && IsByteType(array.ElementType);

        private static bool IsNamedGeneric(ITypeSymbol type, string metadataName) =>
            type is INamedTypeSymbol named && named.OriginalDefinition.ToDisplayString() == metadataName;

        private static bool IsByteType(ITypeSymbol type) => type.SpecialType == SpecialType.System_Byte;
    }

    private enum ProjectionMemberKind
    {
        Unsupported,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Guid,
        UtcInstant,
        Decimal,
        String,
        Bytes,
        ReadOnlyMemoryBytes,
        MemoryBytes,
    }

    private readonly record struct DiagnosticInfo(string DescriptorId, LocationInfo Location, string Message)
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

    private readonly record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var line = location.GetLineSpan();
            return new LocationInfo(line.Path, location.SourceSpan, line.Span);
        }

        public Location ToLocation() => string.IsNullOrEmpty(FilePath) ? Location.None : Location.Create(FilePath, TextSpan, LineSpan);
    }
}
