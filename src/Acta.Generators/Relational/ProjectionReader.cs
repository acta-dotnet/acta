using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Acta.Generators.DbProjectionGenerator;

namespace Acta.Generators;

/// <summary>Reads a <c>[DbProjection]</c> or resolved row type: its constructor, its members, and the binder it can take.</summary>
internal static class ProjectionReader
{
    internal static ProjectionInfo? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var byName = ctx.Attributes.Any(static a => a.NamedArguments.Any(static n => n.Key == "ByName" && n.Value.Value is true));
        return Transform(type, byName);
    }

    // DbProjectionResolver.Resolve<T>(), matched by shape: the resolver is this generator's own output, so the
    // call cannot bind to a symbol while the generator runs.
    internal static bool IsResolveCall(SyntaxNode node) =>
        node is GenericNameSyntax { Identifier.ValueText: "Resolve", TypeArgumentList.Arguments.Count: 1 } name
        && name.Parent is MemberAccessExpressionSyntax access
        && access.Name == name
        && access.Expression is IdentifierNameSyntax { Identifier.ValueText: "DbProjectionResolver" };

    // A type parameter is left to the concrete call that supplies it.
    internal static ProjectionInfo? TransformResolved(GeneratorSyntaxContext ctx, CancellationToken ct) =>
        ctx.SemanticModel.GetTypeInfo(((GenericNameSyntax)ctx.Node).TypeArgumentList.Arguments[0], ct).Type is INamedTypeSymbol type
            ? Transform(type, byName: true)
            : null;

    internal static ProjectionInfo Transform(INamedTypeSymbol type, bool byName)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var typeLocation = type.Locations.FirstOrDefault();
        var typeLoc = typeLocation is null ? default : LocationInfo.From(typeLocation);

        if (type.IsGenericType)
        {
            diagnostics.Add(new DiagnosticInfo(ProjectionDeclaration.Id, typeLoc, $"[DbProjection] type '{type.Name}' cannot be generic."));
        }

        // The binder is a class in the type's namespace: it cannot reach a private nested type, and two nested types of one
        // name would get the same Bind method.
        if (type.ContainingType is not null)
        {
            diagnostics.Add(
                new DiagnosticInfo(ProjectionDeclaration.Id, typeLoc, $"[DbProjection] type '{type.Name}' must be a top-level type.")
            );
        }

        var ctor = SelectConstructor(type, diagnostics, typeLoc);
        var members = ImmutableArray.CreateBuilder<ProjectionMemberInfo>();
        if (ctor is not null)
        {
            if (!IsCallableFromNamespaceBinder(ctor))
            {
                diagnostics.Add(
                    new DiagnosticInfo(
                        ProjectionDeclaration.Id,
                        typeLoc,
                        $"[DbProjection] type '{type.Name}' must expose an internal or public constructor."
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
            Members: members.ToImmutable(),
            Diagnostics: diagnostics.ToImmutable()
        );
    }

    internal static IMethodSymbol? SelectConstructor(
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

    internal static bool IsCopyConstructor(IMethodSymbol ctor, INamedTypeSymbol type) =>
        ctor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type);

    internal static bool IsCallableFromNamespaceBinder(IMethodSymbol ctor) =>
        ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;
}
