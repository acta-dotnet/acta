using System.Reflection;

namespace Acta.Tests.Conformance.Contracts;

/// <summary>Reflection helpers shared by the harness gates.</summary>
public static class HarnessReflection
{
    /// <summary>
    /// A CANDIDATE contract spec - purely structural: abstract, generic, declares at least one own
    /// [Fact], and is NOT assignable to IConformanceMetaSpec&lt;&gt;. Does NOT require [ConformanceSpec]
    /// (so a spec missing metadata is still seen, and fails completeness).
    /// </summary>
    public static bool IsCandidateContractSpec(Type t)
    {
        if (!t.IsAbstract || !t.IsGenericTypeDefinition)
        {
            return false;
        }

        if (IsAssignableToOpenGeneric(t, typeof(IConformanceMetaSpec<>)))
        {
            return false;
        }

        return t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Any(IsFactMethod);
    }

    /// <summary>The [ConformanceSpec] attribute on a candidate, or null if it is missing.</summary>
    public static ConformanceSpecAttribute? ConformanceSpec(Type spec) =>
        (ConformanceSpecAttribute?)spec.GetCustomAttributes().FirstOrDefault(a => a is ConformanceSpecAttribute);

    /// <summary>
    /// The public [Fact]/[Theory] methods declared directly on a spec, in declaration order. Each one
    /// proves a guarantee, surfaced through its DisplayName.
    /// </summary>
    public static IReadOnlyList<MethodInfo> FactMethods(Type spec) =>
        spec.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(IsFactMethod)
            .OrderBy(m => m.MetadataToken)
            .ToList();

    /// <summary>The DisplayName declared on a [Fact]/[Theory] method, or null when none is set.</summary>
    public static string? DisplayName(MethodInfo method)
    {
        var fact = method.GetCustomAttributes().FirstOrDefault(IsFactLike);
        return fact?.GetType().GetProperty("DisplayName")?.GetValue(fact) as string;
    }

    /// <summary>The guarantees a spec proves: the DisplayName of each of its [Fact]/[Theory] methods.</summary>
    public static IReadOnlyList<string> Guarantees(Type spec) =>
        FactMethods(spec).Select(DisplayName).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();

    private static bool IsFactMethod(MethodInfo m) => m.GetCustomAttributes().Any(IsFactLike);

    private static bool IsFactLike(Attribute a)
    {
        for (var t = a.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Name == "FactAttribute")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The open-generic candidate base a concrete type closes over the given fixture, or null.</summary>
    public static Type? ClosedSpecBaseBoundTo(Type concrete, Type fixtureType)
    {
        for (var b = concrete.BaseType; b is not null; b = b.BaseType)
        {
            if (
                b.IsGenericType
                && b.GetGenericArguments() is [var arg]
                && arg == fixtureType
                && IsCandidateContractSpec(b.GetGenericTypeDefinition())
            )
            {
                return b.GetGenericTypeDefinition();
            }
        }

        return null;
    }

    private static bool IsAssignableToOpenGeneric(Type t, Type openGeneric)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == openGeneric)
        {
            return true;
        }

        foreach (var i in t.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        for (var b = t.BaseType; b is not null; b = b.BaseType)
        {
            if (b.IsGenericType && b.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        return false;
    }
}
