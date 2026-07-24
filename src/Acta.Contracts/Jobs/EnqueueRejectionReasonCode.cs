namespace Acta;

/// <summary>
/// Machine-readable reason an enqueue was rejected by a namespace/tenant guard. Plain enum (not a Code
/// family): it is never persisted, so it carries no schema/snapshot footprint. Band is extensible.
/// </summary>
public enum EnqueueRejectionReasonCode : byte
{
    /// <summary>The target namespace is suspended.</summary>
    NamespaceSuspended = 1,

    /// <summary>The referenced tenant is suspended.</summary>
    TenantSuspended = 2,

    /// <summary>The referenced tenant key does not resolve.</summary>
    TenantUnknown = 3,

    /// <summary>The target namespace/job route does not resolve (the owning worker has not registered it).</summary>
    RouteUnknown = 4,

    /// <summary>The target job definition is retired and no longer accepts enqueues.</summary>
    DefinitionRetired = 5,
}
