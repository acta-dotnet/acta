using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Acta.Generators;

/// <summary>
/// Emits <c>DbDataReader</c> binders: for each type marked <c>[DbProjection]</c>, by position unless it
/// asks for <c>ByName</c>, and for every other type the code passes to <c>DbProjectionResolver.Resolve&lt;T&gt;()</c>,
/// by column name. The call is the declaration, so a public model in <c>Acta</c> binds without an attribute.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class DbProjectionGenerator : IIncrementalGenerator
{
    private const string DbProjectionAttr = "Acta.Runtime.DbProjectionAttribute";
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

    private static ProjectionInfo? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var byName = ctx.Attributes.Any(static a => a.NamedArguments.Any(static n => n.Key == "ByName" && n.Value.Value is true));
        return Transform(type, byName, ct);
    }

    // DbProjectionResolver.Resolve<T>(), matched by shape: the resolver is this generator's own output, so the
    // call cannot bind to a symbol while the generator runs.
    private static bool IsResolveCall(SyntaxNode node) =>
        node is GenericNameSyntax { Identifier.ValueText: "Resolve", TypeArgumentList.Arguments.Count: 1 } name
        && name.Parent is MemberAccessExpressionSyntax access
        && access.Name == name
        && access.Expression
            is IdentifierNameSyntax { Identifier.ValueText: "DbProjectionResolver" }
                or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DbProjectionResolver" };

    // A type parameter is left to the concrete call that supplies it.
    private static ProjectionInfo? TransformResolved(GeneratorSyntaxContext ctx, CancellationToken ct) =>
        ctx.SemanticModel.GetTypeInfo(((GenericNameSyntax)ctx.Node).TypeArgumentList.Arguments[0], ct).Type is INamedTypeSymbol type
            ? Transform(type, byName: true, ct)
            : null;

    private static ProjectionInfo Transform(INamedTypeSymbol type, bool byName, CancellationToken ct)
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
            ByName: byName,
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

        spc.AddSource("Acta.Runtime.DbProjectionResolver.g.cs", EmitResolver([.. emittable]));
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
        sb.AppendLine("namespace Acta.Runtime;");
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
        sb.AppendLine();
        sb.Append(RowBindingSource);
        sb.AppendLine("}");
        return sb.ToString();
    }

    // A [DbProjection(ByName = true)] row binds by column name: the ordinals are looked up on the first row a
    // reader hands over and reused for the rest of it, so each query resolves its own binder and never shares
    // one across readers. Every other row binds by position, its columns in SELECT order.
    private const string RowBindingSource = """
            internal static Func<DbDataReader, T> ByPosition<T>(int columns, Func<DbDataReader, int[], T> bind)
            {
                var ordinals = new int[columns];
                for (var i = 0; i < columns; i++)
                {
                    ordinals[i] = i;
                }

                return r => bind(r, ordinals);
            }

            internal static Func<DbDataReader, T> ByName<T>(string projection, string[] columns, Func<DbDataReader, int[], T> bind)
            {
                DbDataReader? reader = null;
                var ordinals = Array.Empty<int>();
                return r =>
                {
                    if (!ReferenceEquals(r, reader))
                    {
                        ordinals = Ordinals(r, projection, columns);
                        reader = r;
                    }

                    return bind(r, ordinals);
                };
            }

            private static int[] Ordinals(DbDataReader r, string projection, string[] columns)
            {
                // Each name read once: on some providers GetName is a native call.
                var actual = new string[r.FieldCount];
                for (var i = 0; i < actual.Length; i++)
                {
                    actual[i] = r.GetName(i);
                }

                var ordinals = new int[columns.Length];
                System.Collections.Generic.List<string>? missing = null;
                for (var c = 0; c < columns.Length; c++)
                {
                    ordinals[c] = -1;
                    for (var i = 0; i < actual.Length; i++)
                    {
                        if (!string.Equals(actual[i], columns[c], StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (ordinals[c] >= 0)
                        {
                            throw new InvalidOperationException($"{projection} reads column '{columns[c]}', which the result holds twice.");
                        }

                        ordinals[c] = i;
                    }

                    if (ordinals[c] < 0)
                    {
                        (missing ??= []).Add(columns[c]);
                    }
                }

                if (missing is not null)
                {
                    throw new InvalidOperationException(
                        $"{projection} reads {string.Join(", ", missing)}, which the result ({string.Join(", ", actual)}) does not have."
                    );
                }

                return ordinals;
            }

        """;

    private static StringBuilder NewSourceBuilder()
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Acta.Runtime;");
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
        var columns = projection.Members.SelectMany(static m => m.Columns).Select(static c => "\"" + c + "\"");
        sb.Append(indent)
            .Append("internal static readonly string[] ")
            .Append(projection.ColumnsName)
            .Append(" = [")
            .Append(string.Join(", ", columns))
            .AppendLine("];");
        sb.AppendLine();
        sb.Append(indent)
            .Append("public static ")
            .Append(projection.TypeFqn)
            .Append(' ')
            .Append(projection.MethodName)
            .AppendLine("(DbDataReader r, int[] o) =>");

        if (projection.Members.Length == 0)
        {
            sb.Append(indent).AppendLine("    new();");
            sb.AppendLine();
            return;
        }

        sb.Append(indent).AppendLine("    new(");
        var column = 0;
        for (var i = 0; i < projection.Members.Length; i++)
        {
            var member = projection.Members[i];
            sb.Append(indent).Append("        ").Append(EscapeIdentifier(member.Name)).Append(": ").Append(BindExpression(member, column));
            sb.AppendLine(i == projection.Members.Length - 1 ? "" : ",");
            column += member.Columns.Length;
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
                .Append("        read = (Func<DbDataReader, T>)(object)global::Acta.Runtime.DbProjectionResolver.")
                .Append(projection.ByName ? "ByName<" : "ByPosition<")
                .Append(projection.TypeFqn)
                .Append(projection.ByName ? ">(\"" + projection.TypeName + "\", " : ">(")
                .Append(binderPrefix)
                .Append(projection.ColumnsName)
                .Append(projection.ByName ? ", " : ".Length, ")
                .Append(binderPrefix)
                .Append(projection.MethodName)
                .AppendLine(");");
            sb.Append(indent).AppendLine("        return;");
            sb.Append(indent).AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append(indent).AppendLine("}");
        sb.AppendLine();
    }

    // `column` indexes the projection's column list; o[column] is that column's ordinal in the reader.
    private static string BindExpression(ProjectionMemberInfo member, int column)
    {
        var ord = "o[" + column.ToString(CultureInfo.InvariantCulture) + "]";
        if (member.Kind == ProjectionMemberKind.Payload)
        {
            // A payload is its format id and its bytes; format id 0 is no payload, null when the member allows it.
            var bytes = "o[" + (column + 1).ToString(CultureInfo.InvariantCulture) + "]";
            var formatId = "Convert.ToByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)";
            var payload =
                "global::Acta.JobPayload.FromBytes(global::Acta.JobPayloadFormat.ForId("
                + formatId
                + "), r.IsDBNull("
                + bytes
                + ") ? [] : (byte[])r.GetValue("
                + bytes
                + "))";
            return member.IsNullable ? "r.IsDBNull(" + ord + ") || " + formatId + " == 0 ? null : " + payload : payload;
        }

        var typed = BindTypedExpression(member, ord);
        return member.IsNullable ? "r.IsDBNull(" + ord + ") ? null : " + typed : typed;
    }

    private static string BindTypedExpression(ProjectionMemberInfo member, string ord)
    {
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
            ProjectionMemberKind.Ref => "new " + member.RefTypeFqn + "(r.GetGuid(" + ord + "))",
            ProjectionMemberKind.UtcInstant => "r.GetDateTimeUtc(" + ord + ")",
            ProjectionMemberKind.Decimal => "Convert.ToDecimal(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.String => "r.GetString(" + ord + ")",
            ProjectionMemberKind.Bytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.ReadOnlyMemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.MemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            _ => throw new InvalidOperationException($"Unsupported projection member kind {member.Kind}."),
        };

        if (member.DecoderFqn is not null)
        {
            return member.DecoderFqn + ".FromId(" + readExpr + ")";
        }

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
        bool ByName,
        ImmutableArray<ContainingTypeInfo> Containers,
        ImmutableArray<ProjectionMemberInfo> Members,
        ImmutableArray<DiagnosticInfo> Diagnostics
    )
    {
        public string ColumnsName => TypeName + "Columns";

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

    private readonly record struct ProjectionMemberInfo(
        string Name,
        bool IsNullable,
        ProjectionMemberKind Kind,
        string? EnumTypeFqn,
        string? DecoderFqn,
        string? RefTypeFqn
    )
    {
        /// <summary>
        /// The columns the member reads: its name in snake_case, and for a payload the format id before it
        /// (<c>input_format_id</c>, <c>input</c>).
        /// </summary>
        public string[] Columns =>
            Kind == ProjectionMemberKind.Payload ? [SnakeCase(Name) + "_format_id", SnakeCase(Name)] : [SnakeCase(Name)];

        private static string SnakeCase(string name)
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

            var refTypeFqn =
                kind == ProjectionMemberKind.Ref ? nonNullableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
            return new ProjectionMemberInfo(parameter.Name, isNullable, kind, enumTypeFqn, decoderFqn, refTypeFqn);
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
            if (type.ToDisplayString() == "Acta.JobPayload")
            {
                return ProjectionMemberKind.Payload;
            }
            if (IsRef(type))
            {
                return ProjectionMemberKind.Ref;
            }
            if (IsByteArray(type))
            {
                return ProjectionMemberKind.Bytes;
            }
            if (IsNamedGeneric(type, "System.ReadOnlyMemory<T>") && IsByteType(((INamedTypeSymbol)type).TypeArguments[0]))
            {
                return ProjectionMemberKind.ReadOnlyMemoryBytes;
            }
            return IsNamedGeneric(type, "System.Memory<T>") && IsByteType(((INamedTypeSymbol)type).TypeArguments[0])
                ? ProjectionMemberKind.MemoryBytes
                : ProjectionMemberKind.Unsupported;
        }

        // A ref is a struct wrapping one Guid: JobRef, AlertRef, WorkerRef.
        private static bool IsRef(ITypeSymbol type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Struct } named
            && named.InstanceConstructors.Any(static c =>
                c.Parameters.Length == 1
                && c.Parameters[0].Type.ToDisplayString() == "System.Guid"
                && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            );

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
        Ref,
        Payload,
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
