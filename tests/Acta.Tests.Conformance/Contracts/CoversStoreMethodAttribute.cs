namespace Acta.Tests.Conformance.Contracts;

/// <summary>
/// Declares one store method a conformance spec covers (one attribute per method). The metadata feeds
/// the store-method coverage gate and the generated conformance-contract inventory. The
/// store must be an internal <c>I*Store</c> interface under <c>Acta.Features</c> or <c>Acta.Services</c>,
/// and the method name should be written with <c>nameof</c> so renames stay compile-checked.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CoversStoreMethodAttribute : Attribute
{
    /// <summary>Create a coverage declaration.</summary>
    public CoversStoreMethodAttribute(Type store, string method)
    {
        Store = store;
        Method = method;
    }

    /// <summary>The covered store interface, e.g. <c>typeof(IJobStore)</c>.</summary>
    public Type Store { get; }

    /// <summary>The covered method name, e.g. <c>nameof(IJobStore.EnqueueAsync)</c>.</summary>
    public string Method { get; }

    /// <summary>The stable coverage identity: <c>{store interface full name}.{method name}</c>.</summary>
    public string Identity => $"{Store.FullName}.{Method}";
}
