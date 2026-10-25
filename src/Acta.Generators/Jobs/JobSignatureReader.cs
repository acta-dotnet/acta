using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>Reads a handler method's signature: its input, its result shape, and the payload format each side infers.</summary>
internal static class JobSignatureReader
{
    internal static ParameterModel AnalyzeParameters(IMethodSymbol method, Location location, List<DiagnosticRecord> diagnostics)
    {
        var parameters = method.Parameters;

        // The input is the parameters before the first framework parameter (JobContext / CancellationToken): none for a
        // zero-input handler, one for TIn, several for the argument record the manifest generates. Any type, including a
        // forbidden one (IServiceProvider, Task, ref-like), is an input candidate that CheckInputParameters rejects.
        var scanFrom = 0;
        while (
            scanFrom < parameters.Length
            && TypeFullName(parameters[scanFrom].Type) is not (JobContextMetadataName or CancellationTokenMetadataName)
        )
        {
            scanFrom++;
        }
        var inputParameters = parameters.Take(scanFrom).ToImmutableArray();

        var hasJobContext = false;
        var hasCancellationToken = false;
        var validOrder = true;

        for (var i = scanFrom; i < parameters.Length; i++)
        {
            var p = parameters[i];
            var name = TypeFullName(p.Type);
            switch (name)
            {
                case JobContextMetadataName:
                    if (hasJobContext || hasCancellationToken)
                    {
                        diagnostics.Add(ManifestDiagnostics.InvalidParameterOrder(method, location));
                        validOrder = false;
                    }
                    hasJobContext = true;
                    break;

                case CancellationTokenMetadataName:
                    hasCancellationToken = true;
                    break;

                default:
                    diagnostics.Add(ManifestDiagnostics.ExtraParameter(method, p, location));
                    validOrder = false;
                    break;
            }
        }

        if (validOrder && hasJobContext && !hasCancellationToken)
        {
            diagnostics.Add(ManifestDiagnostics.JobContextWithoutCancellationToken(method, location));
        }

        return new ParameterModel(
            inputParameters.Length == 1 ? inputParameters[0].Type : null,
            inputParameters,
            hasJobContext,
            hasCancellationToken
        );
    }

    /// <summary>
    /// ACTA0103 for an input parameter no input can be: a forbidden framework type, or, among several parameters that
    /// become one serialized record, an interface, which reads as a dependency to inject rather than a value.
    /// </summary>
    internal static void CheckInputParameters(
        IMethodSymbol method,
        ParameterModel parameters,
        Location location,
        List<DiagnosticRecord> diagnostics
    )
    {
        foreach (var p in parameters.InputParameters)
        {
            if (IsForbiddenInputType(p.Type))
            {
                diagnostics.Add(ManifestDiagnostics.ForbiddenInputType(method, p.Type, location));
            }
            else if (parameters.InputParameters.Length > 1 && p.Type.TypeKind == TypeKind.Interface && !IsEnumerable(p.Type))
            {
                diagnostics.Add(ManifestDiagnostics.InterfaceArgument(method, p, location));
            }
        }
    }

    private static bool IsEnumerable(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Collections_IEnumerable
        || type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable);

    internal static ReturnTypeModel? AnalyzeReturnType(
        IMethodSymbol method,
        Location location,
        ParameterModel? parameters,
        List<DiagnosticRecord> diagnostics
    )
    {
        var returnType = method.ReturnType;
        var returnName = TypeFullName(returnType);

        // void / Task / ValueTask: no durable result.
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            CheckSyncContext(method, parameters, location, diagnostics);
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.Sync);
        }

        if (returnName == TaskMetadataName)
        {
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.Task);
        }

        if (returnName == ValueTaskMetadataName)
        {
            return new ReturnTypeModel(OutputType: null, InvocationKind: JobInvocationKind.ValueTask);
        }

        // Task<T> / ValueTask<T>: async typed result.
        if (returnType is INamedTypeSymbol named && named.IsGenericType)
        {
            var constructedFrom = TypeFullName(named.ConstructedFrom);
            if (constructedFrom == TaskOfTMetadataName)
            {
                var inner = named.TypeArguments[0];
                return BuildResultModel(method, inner, JobInvocationKind.TaskOfT, location, diagnostics);
            }

            if (constructedFrom == ValueTaskOfTMetadataName)
            {
                var inner = named.TypeArguments[0];
                return BuildResultModel(method, inner, JobInvocationKind.ValueTaskOfT, location, diagnostics);
            }
        }

        // Synchronous typed return.
        CheckSyncContext(method, parameters, location, diagnostics);
        return BuildResultModel(method, returnType, JobInvocationKind.SyncOfT, location, diagnostics);
    }

    internal static void CheckSyncContext(
        IMethodSymbol method,
        ParameterModel? parameters,
        Location location,
        List<DiagnosticRecord> diagnostics
    )
    {
        if (parameters?.HasJobContext == true)
        {
            diagnostics.Add(ManifestDiagnostics.SyncWithJobContext(method, location));
        }
    }

    internal static ReturnTypeModel BuildResultModel(
        IMethodSymbol method,
        ITypeSymbol resultType,
        JobInvocationKind invocationKind,
        Location location,
        List<DiagnosticRecord> diagnostics
    )
    {
        var resultName = TypeFullName(resultType);

        // MediatR.Unit collapses to no-result regardless of declared shape.
        if (resultName == MediatRUnitMetadataName)
        {
            // Sync<TUnit> collapses to plain Sync; the async wrappers stay (caller still has to await).
            return new ReturnTypeModel(
                OutputType: null,
                InvocationKind: invocationKind == JobInvocationKind.SyncOfT ? JobInvocationKind.Sync : invocationKind
            );
        }

        if (
            resultName is TaskMetadataName or ValueTaskMetadataName
            || (
                resultType is INamedTypeSymbol nested
                && nested.IsGenericType
                && (
                    TypeFullName(nested.ConstructedFrom) == TaskOfTMetadataName
                    || TypeFullName(nested.ConstructedFrom) == ValueTaskOfTMetadataName
                )
            )
        )
        {
            diagnostics.Add(ManifestDiagnostics.NestedAwaitableReturn(method, resultType, location));
        }

        return new ReturnTypeModel(resultType, invocationKind);
    }

    internal static bool IsForbiddenInputType(ITypeSymbol type)
    {
        if (TypeFullName(type) is IServiceProviderMetadataName or TaskMetadataName or ValueTaskMetadataName)
        {
            return true;
        }

        // Task<T> / ValueTask<T> as input.
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var ctor = TypeFullName(named.ConstructedFrom);
            if (ctor == TaskOfTMetadataName || ctor == ValueTaskOfTMetadataName)
            {
                return true;
            }
        }

        // Ref-like types (Span<T>, ref struct).
        return type.IsRefLikeType;
    }

    /// <summary>
    /// True when `new T()` is safe AND the type has no instance state (canonical: empty record).
    /// Used to detect pure dispatch-key DTOs the runtime can fabricate without a payload.
    /// </summary>
    internal static bool IsParameterlessDataLess(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol nt)
        {
            return false;
        }
        if (type.IsRefLikeType || type.IsAbstract || type.TypeKind == TypeKind.Interface)
        {
            return false;
        }

        // Primitives and other intrinsic scalars (int, double, DateTime, ...) and enums carry a
        // value despite having an implicit parameterless ctor and no Roslyn-visible instance field.
        // They are never data-less; classifying them as such would map them to the 'none' format and
        // silently drop the input. Only user-defined empty records/structs reach the field scan below.
        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        // Value types always have an implicit parameterless ctor (or one explicit). For reference
        // types require an accessible parameterless instance ctor: public or internal.
        var hasAccessibleParameterlessCtor =
            type.IsValueType
            || nt.InstanceConstructors.Any(c =>
                c.Parameters.IsEmpty && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            );
        if (!hasAccessibleParameterlessCtor)
        {
            return false;
        }

        for (var t = (ITypeSymbol?)nt; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            // A get-only property is state too: a constructor can set it, and a type from a
            // referenced assembly shows no private backing field.
            if (t.GetMembers().Any(m => m is IFieldSymbol or IPropertySymbol && !m.IsStatic && !m.IsImplicitlyDeclared))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when every name the runtime stores for this contract type is printable ASCII: its namespace, its own
    /// and its containing types' names, its type arguments, and an array's element type.
    /// </summary>
    internal static bool IsAsciiContractType(ITypeSymbol type) =>
        type switch
        {
            IArrayTypeSymbol array => IsAsciiContractType(array.ElementType),
            INamedTypeSymbol named => IsPrintableAscii(TypeFullName(named))
                && (named.ContainingType is null || IsAsciiContractType(named.ContainingType))
                && named.TypeArguments.All(IsAsciiContractType),
            _ => IsPrintableAscii(type.Name),
        };

    private static bool IsPrintableAscii(string value) => value.All(c => c is >= (char)0x20 and <= (char)0x7E);

    internal static bool HasAccessibleParameterlessCtor(ITypeSymbol type)
    {
        if (type.IsValueType)
        {
            return true;
        }
        return type is not INamedTypeSymbol nt || type.IsAbstract || type.TypeKind == TypeKind.Interface
            ? false
            : nt.InstanceConstructors.Any(c =>
                c.Parameters.IsEmpty && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            );
    }

    /// <summary>
    /// Arity-suffixed metadata name (e.g. `System.Threading.Tasks.Task`1`) for comparison
    /// against the *MetadataName constants in ActaManifestGenerator.
    /// </summary>
    internal static string TypeFullName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var ns = named.ContainingNamespace.IsGlobalNamespace ? null : named.ContainingNamespace.ToDisplayString();
            var prefix = ns is null ? "" : ns + ".";
            return prefix + named.MetadataName;
        }
        return type.ToDisplayString();
    }

    internal static string InferPayloadFormatName(ITypeSymbol type, bool isInput)
    {
        // A parameterless data-less contract (e.g. `public sealed record FetchJoke;`) is a pure
        // dispatch key: no instance fields or properties. Serializing it to `{}`
        // is wasted bytes; map it to `none`. The dispatch class fabricates `new T()` at execute
        // time so the handler still receives a non-null instance.
        if (isInput && IsParameterlessDataLess(type))
        {
            return "none";
        }

        // byte[] and ReadOnlyMemory<byte> pass through as raw bytes; every other value, scalars included, is JSON.
        var isByteArray = type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_Byte;
        var isByteMemory =
            type is INamedTypeSymbol nm
            && nm.IsGenericType
            && TypeFullName(nm.ConstructedFrom) == "System.ReadOnlyMemory`1"
            && nm.TypeArguments.Length == 1
            && nm.TypeArguments[0].SpecialType == SpecialType.System_Byte;
        return isByteArray || isByteMemory ? "bytes" : "json";
    }
}
